// MIT License
//
// Copyright (c) 2025 - 2026 MonsterSyncDrive.Com
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using MonsterSyncDrive.Library.FTP;
using MonsterSyncDrive.Library.FTP.Protocol;
using System.Net.Sockets;

namespace MonsterSyncDrive.FTP;

/// <summary>数据传输期间的控制命令协调及不会丢失回复的 ABOR 事务。</summary>
public sealed partial class FtpClient
{
    /// <summary>保护一个控制事务的写入和所有关联回复，任何时刻只有一个控制读取者。</summary>
    private readonly SemaphoreSlim _controlGate = new(1, 1);

    /// <summary>保护活动数据事务发布与下一个事务开始通知。</summary>
    private readonly object _transferStateLock = new();

    /// <summary>由开始数据事务唤醒等待会话锁的 ABOR/STAT，避免它们排在长传输后永远不能执行。</summary>
    private TaskCompletionSource _transferBegan = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>当前独占会话的数据事务，准备阶段也会公布，以免 ABOR 抢走其预备回复。</summary>
    private FtpActiveTransfer? _activeTransfer;

    /// <summary>供无反射并发测试确定唯一完成读取者已进入等待阶段。</summary>
    internal bool IsAwaitingTransferCompletion
    {
        get { lock (_transferStateLock) return _activeTransfer?.CompletionReaderActive == true; }
    }

    /// <summary>使用带内 ABOR 中止数据操作并保留控制会话；并发调用共享同一次中止结果。</summary>
    public Task<FtpAbortResult> AbortAsync(CancellationToken cancellationToken = default) =>
        AbortAsync(new FtpAbortOptions(), cancellationToken);

    /// <summary>
    /// 执行可选 Telnet IP/SYNCH 的 ABOR。只有确认完整消费数据结束、ABOR 和内部 NOOP 的回复后才返回。
    /// 阶段超时或服务器拒绝导致无法确认同步时会关闭控制连接，而不会返回虚假的成功状态。
    /// </summary>
    public async Task<FtpAbortResult> AbortAsync(FtpAbortOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsConnected) throw new InvalidOperationException("请先登录 FTP 会话。");
        if (options.SendTelnetInterrupt && (_controlStream is not NetworkStream || _securityMechanism is not null))
            throw new InvalidOperationException("Telnet IP/SYNCH 只能用于没有 TLS 或 RFC 2228 命令保护的明文连接。");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var (transfer, ownsSession) = await AcquireTransferControlTargetAsync(linked.Token).ConfigureAwait(false);
        var ownsControl = false;
        var ownsAbort = false;
        try
        {
            while (transfer is not null)
            {
                await transfer.Ready.WaitAsync(linked.Token).ConfigureAwait(false);
                // 例如 MLSD 被拒绝后立即回退 LIST：准备失败时没有已接受的数据命令，ABOR 不应插入回退事务。
                if (!transfer.Started)
                    return new FtpAbortResult(false, false, Array.Empty<FtpReply>());
                if (!transfer.TryRequestAbort(options))
                {
                    if (transfer.AbortRequested)
                        return await transfer.AbortCompletion.WaitAsync(linked.Token).ConfigureAwait(false);
                    // 数据最终完成刚刚抢先确认。重新取得空闲会话所有权，不能把ABOR插入其他会话事务的两步命令之间。
                    await Task.Yield();
                    (transfer, ownsSession) = await AcquireTransferControlTargetAsync(linked.Token).ConfigureAwait(false);
                }
                else
                {
                    ownsAbort = true;
                    if (transfer.ReaderOwnsAbort)
                        return await transfer.AbortCompletion.WaitAsync(linked.Token).ConfigureAwait(false);
                    break;
                }
            }

            await _controlGate.WaitAsync(linked.Token).ConfigureAwait(false);
            ownsControl = true;
            var replies = await FtpTimeout.RunAsync(async token =>
            {
                var channel = _channel ?? throw new ObjectDisposedException(nameof(FtpClient));
                if (options.SendTelnetInterrupt) await SendTelnetInterruptAsync(token).ConfigureAwait(false);
                await SendSecuredCommandAsync(channel, "ABOR", null, token).ConfigureAwait(false);
                if (transfer is not null)
                {
                    // 先告知服务器正在中止，再关闭数据连接；提前关闭未读完的 TCP 可能触发 RST，
                    // 导致服务器在收到 ABOR 前就把整个控制会话当作故障断开。
                    transfer.CancelData();
                    CloseAbortedDataChannel();
                }
                await SendSecuredCommandAsync(channel, "NOOP", null, token).ConfigureAwait(false);
                return await DrainControlBarrierAsync(channel, transfer, "ABOR", token).ConfigureAwait(false);
            }, _options.CommandTimeout, linked.Token, "ABOR 同步").ConfigureAwait(false);

            var rejected = replies.FirstOrDefault(static reply => reply.IsError && reply.Code != 426);
            if (rejected is not null) throw new FtpCommandException("ABOR", rejected);
            if (!replies.Any(static reply => reply.Code is 225 or 226))
                throw new FtpProtocolException("ABOR 没有返回 225/226 完成回复。");
            _pendingSequence = false;
            var result = new FtpAbortResult(transfer is not null, options.SendTelnetInterrupt, replies);

            // 公布 ABOR 同步成功后再释放控制锁。流释放回调会归还会话锁，不能在控制锁内反向等待它。
            _controlGate.Release();
            ownsControl = false;
            if (transfer is not null)
            {
                await transfer.CompleteAbortAsync(result).ConfigureAwait(false);
                if (transfer.HasReleaseOwner) DetachActiveTransfer(transfer);
            }
            return result;
        }
        catch (Exception exception)
        {
            if (ownsAbort || ownsControl)
            {
                transfer?.FailAbort(exception);
                CloseConnection();
            }
            throw;
        }
        finally
        {
            if (ownsControl) _controlGate.Release();
            if (ownsSession) _sessionGate.Release();
        }
    }

    /// <summary>
    /// 在数据操作期间查询 STAT，不排在数据流占用的会话锁之后。服务器先返回的数据完成回复会保存给原事务。
    /// 不存在数据操作时，也可作为普通 STAT 查询使用。
    /// </summary>
    public async Task<FtpReply> GetTransferStatusAsync(string? path = null, CancellationToken cancellationToken = default)
    {
        if (path is not null) FtpPath.Validate(path);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsConnected) throw new InvalidOperationException("请先登录 FTP 会话。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var (transfer, ownsSession) = await AcquireTransferControlTargetAsync(linked.Token).ConfigureAwait(false);
        var ownsControl = false;
        var statusSent = false;
        try
        {
            while (transfer is not null)
            {
                await transfer.Ready.WaitAsync(linked.Token).ConfigureAwait(false);
                if (transfer.Started) break;
                // 准备失败的旧事务不可代表紧接着开始的MLSD→LIST回退；重新选择目标及其会话锁所有权。
                await Task.Yield();
                (transfer, ownsSession) = await AcquireTransferControlTargetAsync(linked.Token).ConfigureAwait(false);
            }
            if (transfer is not null && transfer.TryQueueStatus(path, out var queued))
                return await queued!.Reply.WaitAsync(linked.Token).ConfigureAwait(false);
            if (transfer is not null && !transfer.CompletionReaderBegan.IsCompleted)
            {
                using var race = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                var control = _controlGate.WaitAsync(race.Token);
                await Task.WhenAny(control, transfer.CompletionReaderBegan.WaitAsync(race.Token)).ConfigureAwait(false);
                race.Cancel();
                try { await control.ConfigureAwait(false); ownsControl = true; }
                catch (OperationCanceledException) { }
                linked.Token.ThrowIfCancellationRequested();
                if (!ownsControl && transfer.TryQueueStatus(path, out queued))
                    return await queued!.Reply.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            if (!ownsControl)
            {
                await _controlGate.WaitAsync(linked.Token).ConfigureAwait(false);
                ownsControl = true;
            }
            return await FtpTimeout.RunAsync(async token =>
            {
                var channel = _channel ?? throw new ObjectDisposedException(nameof(FtpClient));
                statusSent = true;
                await SendSecuredCommandAsync(channel, "STAT", path, token).ConfigureAwait(false);
                if (transfer is null)
                {
                    while (true)
                    {
                        var reply = await ReadSecuredReplyAsync(channel, token).ConfigureAwait(false);
                        if (reply.Code == 110) { ProcessRestartMarkerReply(reply); continue; }
                        return reply;
                    }
                }
                await SendSecuredCommandAsync(channel, "NOOP", null, token).ConfigureAwait(false);
                var replies = await DrainControlBarrierAsync(channel, transfer, "STAT", token).ConfigureAwait(false);
                return replies.FirstOrDefault(static reply => reply.Code is not (226 or 250 or 426))
                    ?? throw new FtpProtocolException("STAT 未返回独立状态回复。");
            }, _options.CommandTimeout, linked.Token, "STAT 状态查询").ConfigureAwait(false);
        }
        catch { if (statusSent) CloseConnection(); throw; }
        finally
        {
            if (ownsControl) _controlGate.Release();
            if (ownsSession) _sessionGate.Release();
        }
    }

    /// <summary>数据命令准备开始时公布事务；收到预备回复后才允许 ABOR 取消它的数据方向。</summary>
    private FtpActiveTransfer BeginActiveTransfer(CancellationToken operationToken)
    {
        var transfer = new FtpActiveTransfer(operationToken);
        TaskCompletionSource began;
        lock (_transferStateLock)
        {
            if (_activeTransfer is not null)
                throw new InvalidOperationException("上一项 FTP 数据事务尚未结束。");
            _activeTransfer = transfer;
            began = _transferBegan;
            _transferBegan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        began.TrySetResult();
        return transfer;
    }

    /// <summary>准备失败时移除尚未接受的数据事务，允许 MLSD→LIST 等兼容回退继续。</summary>
    private void EndActiveTransferPreparation()
    {
        FtpActiveTransfer? transfer;
        lock (_transferStateLock) transfer = _activeTransfer;
        if (transfer is null || (transfer.Started && transfer.AbortRequested)) return;
        transfer.FailPreparation();
        DetachActiveTransfer(transfer);
    }

    /// <summary>返回当前数据事务的专用取消标记；普通控制阶段仍使用外层 token。</summary>
    private CancellationToken GetActiveTransferToken(CancellationToken fallback)
    {
        lock (_transferStateLock) return _activeTransfer?.Token ?? fallback;
    }

    /// <summary>把打开的远程流释放责任登记到当前事务，使客户端级 ABOR 能在调用者不再读写时归还会话锁。</summary>
    private async Task RegisterActiveTransferRelease(Func<Task> release)
    {
        ArgumentNullException.ThrowIfNull(release);
        FtpActiveTransfer? transfer;
        lock (_transferStateLock) transfer = _activeTransfer;
        if (transfer is null) return;
        await transfer.RegisterRelease(release).ConfigureAwait(false);
        if (transfer.AbortCompletion.IsCompletedSuccessfully) DetachActiveTransfer(transfer);
    }

    /// <summary>读取数据最终回复或消费STAT缓存，并在已有读取者等待226时由该读取者接管ABOR。</summary>
    private async Task<FtpReply> ReadTransferCompletionAsync(CancellationToken cancellationToken)
    {
        FtpActiveTransfer? transfer;
        lock (_transferStateLock) transfer = _activeTransfer;
        if (transfer?.AbortRequested == true)
            throw new FtpTransferAbortedException(await transfer.AbortCompletion.WaitAsync(_lifetime.Token).ConfigureAwait(false));

        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var entered = false;
        try
        {
            if (transfer is null || (entered = transfer.TryEnterCompletionReader()))
            {
                var channel = _channel ?? throw new ObjectDisposedException(nameof(FtpClient));
                while (true)
                {
                    FtpReply? reply = null;
                    Task<FtpReply>? pending = null;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    if (_options.CommandTimeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(_options.CommandTimeout);
                    try
                    {
                        if (transfer is null || !transfer.TryTakeCompletion(out reply))
                        {
                            pending = ReadSecuredReplyAsync(channel, deadline.Token);
                            if (transfer is not null)
                            {
                                // 不取消已开始的读：它可能已经读到半条状态行。收到中止请求后沿用同一reader消费回复。
                                while (true)
                                {
                                    var statusSignal = transfer.StatusRequestedSignal;
                                    await Task.WhenAny(pending, transfer.AbortRequestedSignal, statusSignal).ConfigureAwait(false);
                                    if (transfer.AbortRequested && transfer.ReaderOwnsAbort)
                                    {
                                        if (_options.CommandTimeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(_options.CommandTimeout);
                                        var aborted = await AbortFromCompletionReaderAsync(channel, transfer, pending, null, deadline.Token).ConfigureAwait(false);
                                        throw new FtpTransferAbortedException(aborted);
                                    }
                                    if (statusSignal.IsCompleted && transfer.TryTakeStatus(out var status))
                                    {
                                        if (_options.CommandTimeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(_options.CommandTimeout);
                                        await ProcessActiveStatusRequestAsync(channel, transfer, status!, pending, deadline.Token).ConfigureAwait(false);
                                        pending = null;
                                        break;
                                    }
                                    if (pending.IsCompleted) break;
                                }
                                if (pending is null) continue;
                            }
                            reply = await pending.ConfigureAwait(false);
                        }
                        if (reply!.Code == 110) { ProcessRestartMarkerReply(reply); continue; }
                        IReadOnlyList<FtpTransferStatusRequest> pendingStatuses = Array.Empty<FtpTransferStatusRequest>();
                        if (transfer is null || transfer.TryFinishNormally(out pendingStatuses))
                        {
                            try
                            {
                                Expect("TRANSFER", reply, 226, 250);
                                foreach (var status in pendingStatuses)
                                {
                                    if (_options.CommandTimeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(_options.CommandTimeout);
                                    await ProcessActiveStatusRequestAsync(channel, transfer!, status, null, deadline.Token).ConfigureAwait(false);
                                }
                            }
                            catch (Exception failure)
                            {
                                foreach (var status in pendingStatuses) status.Fail(failure);
                                throw;
                            }
                            _pendingSequence = false;
                            if (transfer is not null) DetachActiveTransfer(transfer);
                            return reply;
                        }
                        // 最终回复和Abort请求同时到达，Abort先取得状态锁：226仍交给中止事务收集。
                        if (_options.CommandTimeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(_options.CommandTimeout);
                        var result = await AbortFromCompletionReaderAsync(channel, transfer, null, reply, deadline.Token).ConfigureAwait(false);
                        throw new FtpTransferAbortedException(result);
                    }
                    catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
                    {
                        throw new TimeoutException("FTP 传输完成或ABOR回复超时。", exception);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            if (transfer?.ReaderOwnsAbort == true && !transfer.AbortCompletion.IsCompletedSuccessfully)
                transfer.FailAbort(exception);
            throw;
        }
        finally
        {
            if (entered) transfer!.LeaveCompletionReader();
            _controlGate.Release();
        }

        // ABOR在本reader进入控制锁前已取得所有权，此时先归还控制锁再等待其结果。
        throw new FtpTransferAbortedException(await transfer!.AbortCompletion.WaitAsync(_lifetime.Token).ConfigureAwait(false));
    }

    /// <summary>
    /// 已持控制锁的完成读取者执行ABOR，复用任何在途读取任务，杜绝取消半行回复后重读导致的分帧错误。
    /// 此路径的流完成回调已经在执行，不能递归调用同一回调；它会在异常传播后自行归还会话锁。
    /// </summary>
    private async Task<FtpAbortResult> AbortFromCompletionReaderAsync(FtpControlChannel channel,
        FtpActiveTransfer transfer, Task<FtpReply>? pendingReply, FtpReply? earlierReply, CancellationToken cancellationToken)
    {
        var options = transfer.AbortOptions;
        if (options.SendTelnetInterrupt) await SendTelnetInterruptAsync(cancellationToken).ConfigureAwait(false);
        await SendSecuredCommandAsync(channel, "ABOR", null, cancellationToken).ConfigureAwait(false);
        transfer.CancelData();
        CloseAbortedDataChannel();
        await SendSecuredCommandAsync(channel, "NOOP", null, cancellationToken).ConfigureAwait(false);
        var replies = await DrainControlBarrierAsync(channel, transfer, "ABOR", cancellationToken, pendingReply, earlierReply).ConfigureAwait(false);
        var rejected = replies.FirstOrDefault(static reply => reply.IsError && reply.Code != 426);
        if (rejected is not null) throw new FtpCommandException("ABOR", rejected);
        if (!replies.Any(static reply => reply.Code is 225 or 226))
            throw new FtpProtocolException("ABOR没有返回225/226完成回复。");
        _pendingSequence = false;
        var result = new FtpAbortResult(true, options.SendTelnetInterrupt, replies);
        await transfer.CompleteAbortAsync(result, releaseOwner: false).ConfigureAwait(false);
        DetachActiveTransfer(transfer);
        return result;
    }

    /// <summary>由已有的唯一完成读取者发送STAT并用NOOP屏障区分先到的数据完成回复。</summary>
    private async Task ProcessActiveStatusRequestAsync(FtpControlChannel channel, FtpActiveTransfer transfer,
        FtpTransferStatusRequest request, Task<FtpReply>? pendingReply, CancellationToken cancellationToken)
    {
        try
        {
            await SendSecuredCommandAsync(channel, "STAT", request.Path, cancellationToken).ConfigureAwait(false);
            await SendSecuredCommandAsync(channel, "NOOP", null, cancellationToken).ConfigureAwait(false);
            var replies = await DrainControlBarrierAsync(channel, transfer, "STAT", cancellationToken, pendingReply).ConfigureAwait(false);
            var status = replies.FirstOrDefault(static reply => reply.Code is not (226 or 250 or 426))
                ?? throw new FtpProtocolException("STAT未返回独立状态回复。");
            request.Complete(status);
        }
        catch (Exception exception) { request.Fail(exception); throw; }
    }

    /// <summary>
    /// 归一化数据错误。只有显式 ABOR 已完成同步时转换为保留会话的专用异常；
    /// 普通用户取消、网络故障、超时仍返回原异常并由外层关闭会话。
    /// </summary>
    private async Task<Exception> HandleTransferFailureAsync(Exception exception)
    {
        FtpActiveTransfer? transfer;
        lock (_transferStateLock) transfer = _activeTransfer;
        if (transfer?.AbortRequested != true) return exception;
        try
        {
            var result = await transfer.AbortCompletion.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            DetachActiveTransfer(transfer);
            return new FtpTransferAbortedException(result);
        }
        catch (Exception abortFailure) { return abortFailure; }
    }

    /// <summary>控制连接关闭或 REIN 清理时取消旧事务，并唤醒所有 ABOR/STAT 等待者。</summary>
    private void ResetActiveTransferState()
    {
        FtpActiveTransfer? transfer;
        lock (_transferStateLock) { transfer = _activeTransfer; _activeTransfer = null; }
        if (transfer is null) return;
        transfer.Close();
        transfer.Dispose();
    }

    /// <summary>仅移除指定事务，避免旧事务清理误伤后续连接上的新传输。</summary>
    private void DetachActiveTransfer(FtpActiveTransfer transfer)
    {
        lock (_transferStateLock)
        {
            if (ReferenceEquals(_activeTransfer, transfer)) _activeTransfer = null;
        }
        transfer.Dispose();
    }

    /// <summary>中止数据通道并释放分帧/压缩及TLS资源，不发送压缩尾部，也不改变控制连接的PROT策略。</summary>
    private void CloseAbortedDataChannel()
    {
        Interlocked.Exchange(ref _dataClient, null)?.Dispose();
        Interlocked.Exchange(ref _dataStream, null)?.Dispose();
        var application = Interlocked.Exchange(ref _dataApplicationStream, null);
        if (application is FtpDataWriteStream writer) writer.Abort();
        application?.Dispose();
        Interlocked.Exchange(ref _dataTlsSession, null)?.Dispose();
    }

    /// <summary>
    /// 等待“当前会话空闲”或“数据事务开始”二者之一。若只排会话锁，ABOR 会被长传输永远挡住。
    /// 取消丢失竞争的锁等待时仍检查它是否刚好成功，确保信号量不会被无声占住。
    /// </summary>
    private async Task<(FtpActiveTransfer? Transfer, bool OwnsSession)> AcquireTransferControlTargetAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            FtpActiveTransfer? current;
            Task began;
            lock (_transferStateLock) { current = _activeTransfer; began = _transferBegan.Task; }
            if (current is not null) return (current, false);
            using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var session = _sessionGate.WaitAsync(race.Token);
            var started = began.WaitAsync(race.Token);
            await Task.WhenAny(session, started).ConfigureAwait(false);
            race.Cancel();
            var acquired = false;
            try { await session.ConfigureAwait(false); acquired = true; }
            catch (OperationCanceledException) { }
            if (cancellationToken.IsCancellationRequested)
            {
                if (acquired) _sessionGate.Release();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (acquired) return (null, true);
        }
    }

    /// <summary>
    /// 收集命令及数据完成回复直至 NOOP 的 200。ABOR 的单个 226 既可能是旧传输完成，
    /// 也可能是 ABOR 完成；屏障避免依靠短超时猜测后面是否还会有另一个 226。
    /// </summary>
    private async Task<IReadOnlyList<FtpReply>> DrainControlBarrierAsync(FtpControlChannel channel,
        FtpActiveTransfer? transfer, string command, CancellationToken cancellationToken,
        Task<FtpReply>? pendingReply = null, FtpReply? earlierReply = null)
    {
        var replies = new List<FtpReply>();
        for (var index = 0; index < _options.MaxReplyLines; index++)
        {
            FtpReply reply;
            if (earlierReply is not null) { reply = earlierReply; earlierReply = null; }
            else if (pendingReply is not null) { reply = await pendingReply.ConfigureAwait(false); pendingReply = null; }
            else reply = await ReadSecuredReplyAsync(channel, cancellationToken).ConfigureAwait(false);
            if (reply.Code == 110) { ProcessRestartMarkerReply(reply); continue; }
            if (reply.Code == 200) return replies.AsReadOnly();
            if (reply.Code == 421) throw new FtpCommandException(command, reply);
            if (reply.IsPreliminary || reply.IsIntermediate)
                throw new FtpProtocolException(command + " 同步期间收到不允许的中间回复。");
            replies.Add(reply);
            if (command == "STAT" && reply.Code is 226 or 250 or 426) transfer?.QueueCompletion(reply);
        }
        throw new FtpProtocolException(command + " 同步回复数超过配置限制。");
    }

    /// <summary>在未加密控制通道发送 Telnet IP 及带 TCP urgent 标记的 Data Mark。</summary>
    private async Task SendTelnetInterruptAsync(CancellationToken cancellationToken)
    {
        var stream = _controlStream ?? throw new ObjectDisposedException(nameof(FtpClient));
        await stream.WriteAsync(new byte[] { 255, 244, 255 }, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        var socket = _controlClient?.Client ?? throw new ObjectDisposedException(nameof(FtpClient));
        var sent = await socket.SendAsync(new byte[] { 242 }.AsMemory(), SocketFlags.OutOfBand, cancellationToken).ConfigureAwait(false);
        if (sent != 1) throw new IOException("Telnet SYNCH 的 Data Mark 未完整发送。");
    }
}