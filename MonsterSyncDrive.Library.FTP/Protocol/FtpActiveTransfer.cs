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

namespace MonsterSyncDrive.Library.FTP.Protocol;

/// <summary>
/// 保存单次数据事务的并发状态。所有状态转换在短锁内完成，控制读取本身由客户端控制锁串行化。
/// ABOR 的完成任务先置为成功，再调用流释放回调，避免释放回调反向等待 ABOR 造成死锁。
/// </summary>
internal sealed class FtpActiveTransfer : IDisposable
{
    /// <summary>保护此事务的状态和可选释放回调。</summary>
    private readonly object _sync = new();

    /// <summary>只取消数据 I/O，不取消 ABOR 用来恢复同步的控制读取。</summary>
    private readonly CancellationTokenSource _dataCancellation;

    /// <summary>数据命令收到预备回复或准备失败时唤醒等待的 ABOR/STAT。</summary>
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>唯一 ABOR 事务的同步结果。</summary>
    private readonly TaskCompletionSource<FtpAbortResult> _abort = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>通知已经等待最终回复的唯一控制读取者，改由它发送并消费ABOR事务。</summary>
    private readonly TaskCompletionSource _abortRequestedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>通知竞争控制锁的STAT调用已有唯一完成读取者可以接管查询。</summary>
    private readonly TaskCompletionSource _completionReaderBegan = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>等待唯一完成读取者处理的STAT请求。</summary>
    private readonly Queue<FtpTransferStatusRequest> _statusRequests = new();

    /// <summary>当前STAT请求队列由空变非空的信号。</summary>
    private TaskCompletionSource _statusRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>由传输期间 STAT 提前读到的数据最终回复。</summary>
    private readonly Queue<FtpReply> _completionReplies = new();

    /// <summary>打开流的完成回调；高层复制操作则通过取消其 I/O 自行退出。</summary>
    private Func<Task>? _releaseOwner;

    /// <summary>已经启动的唯一持有者释放任务，处理注册与ABOR完成的并发竞态。</summary>
    private Task? _ownerReleaseTask;

    /// <summary>是否已经接受数据命令。</summary>
    private bool _started;

    /// <summary>是否已结束数据事务或准备失败。</summary>
    private bool _terminal;

    /// <summary>是否有 ABOR 调用取得该事务的中止所有权。</summary>
    private bool _abortRequested;

    /// <summary>是否已有普通数据完成读取者持有控制锁。</summary>
    private bool _completionReaderActive;

    /// <summary>中止请求是否已交给该完成读取者处理，避免两个读取者竞争同一控制流。</summary>
    private bool _readerOwnsAbort;

    /// <summary>唯一ABOR所有者选择的Telnet选项。</summary>
    private FtpAbortOptions _abortOptions = new();

    /// <summary>防止重复释放取消源。</summary>
    private int _disposed;

    /// <summary>创建独立数据取消标记，并绑定外层操作的取消。</summary>
    internal FtpActiveTransfer(CancellationToken operationToken) =>
        _dataCancellation = CancellationTokenSource.CreateLinkedTokenSource(operationToken);

    /// <summary>用于主动 accept、数据 TLS 及读写的取消标记。</summary>
    internal CancellationToken Token => _dataCancellation.Token;

    /// <summary>控制命令准备阶段结束信号；等待时不可持有控制读取锁。</summary>
    internal Task Ready => _ready.Task;

    /// <summary>只读的 ABOR 完成任务。</summary>
    internal Task<FtpAbortResult> AbortCompletion => _abort.Task;

    /// <summary>显式中止请求到达信号，不取消已经进行中的控制回复读取。</summary>
    internal Task AbortRequestedSignal => _abortRequestedSignal.Task;

    /// <summary>唯一完成读取者已经取得控制锁的信号。</summary>
    internal Task CompletionReaderBegan => _completionReaderBegan.Task;

    /// <summary>当前存在待处理STAT查询的信号。</summary>
    internal Task StatusRequestedSignal { get { lock (_sync) return _statusRequested.Task; } }

    /// <summary>中止请求是否由已有控制完成读取者执行。</summary>
    internal bool ReaderOwnsAbort { get { lock (_sync) return _readerOwnsAbort; } }

    /// <summary>完成读取者是否已取得控制锁。</summary>
    internal bool CompletionReaderActive { get { lock (_sync) return _completionReaderActive; } }

    /// <summary>中止事务已经选定的控制发送选项。</summary>
    internal FtpAbortOptions AbortOptions { get { lock (_sync) return _abortOptions; } }

    /// <summary>数据命令是否已被服务器接受。</summary>
    internal bool Started { get { lock (_sync) return _started; } }

    /// <summary>此事务是否已经请求显式中止。</summary>
    internal bool AbortRequested { get { lock (_sync) return _abortRequested; } }

    /// <summary>此事务是否由需要主动归还会话锁的远程流持有。</summary>
    internal bool HasReleaseOwner { get { lock (_sync) return _releaseOwner is not null; } }

    /// <summary>数据命令收到 125/150 后完成准备阶段。</summary>
    internal void MarkReady()
    {
        lock (_sync) { if (!_terminal) _started = true; }
        _ready.TrySetResult();
    }

    /// <summary>准备失败时唤醒等待者；未开始的数据命令不能让 ABOR 抢占其兼容回退命令。</summary>
    internal void FailPreparation()
    {
        lock (_sync) _terminal = true;
        _ready.TrySetResult();
    }

    /// <summary>在尚未正常结束时取得唯一中止权；并发 ABOR 共享同一结果。</summary>
    internal bool TryRequestAbort(FtpAbortOptions options)
    {
        lock (_sync)
        {
            if (_terminal && !_abortRequested) return false;
            if (_abortRequested) return false;
            _abortRequested = true;
            _readerOwnsAbort = _completionReaderActive;
            _abortOptions = options;
        }
        _abortRequestedSignal.TrySetResult();
        return true;
    }

    /// <summary>在普通读取者持有控制锁后公布它的所有权；中止已开始时不可再启动第二条控制读取。</summary>
    internal bool TryEnterCompletionReader()
    {
        lock (_sync)
        {
            if (_abortRequested || _terminal) return false;
            _completionReaderActive = true;
        }
        _completionReaderBegan.TrySetResult();
        return true;
    }

    /// <summary>普通完成读取者离开控制锁之前清除所有权。</summary>
    internal void LeaveCompletionReader()
    {
        lock (_sync) _completionReaderActive = false;
    }

    /// <summary>取消数据 I/O；必须在服务器已接受数据命令后才调用。</summary>
    internal void CancelData()
    {
        try { _dataCancellation.Cancel(); }
        catch (ObjectDisposedException) { /* 正常完成与 ABOR 竞态时取消源可能已经释放。 */ }
    }

    /// <summary>缓存由控制侧命令提前读取的数据完成回复。</summary>
    internal void QueueCompletion(FtpReply reply)
    {
        lock (_sync) _completionReplies.Enqueue(reply);
    }

    /// <summary>取出一条缓存的完成回复，保持服务器顺序。</summary>
    internal bool TryTakeCompletion(out FtpReply? reply)
    {
        lock (_sync)
        {
            if (_completionReplies.TryDequeue(out var value)) { reply = value; return true; }
            reply = null;
            return false;
        }
    }

    /// <summary>原子确认正常完成；若 ABOR 已抢先请求则由中止事务负责收尾。</summary>
    internal bool TryFinishNormally(out IReadOnlyList<FtpTransferStatusRequest> pendingStatuses)
    {
        lock (_sync)
        {
            pendingStatuses = Array.Empty<FtpTransferStatusRequest>();
            if (_abortRequested) return false;
            _terminal = true;
            pendingStatuses = _statusRequests.ToArray();
            _statusRequests.Clear();
            return true;
        }
    }

    /// <summary>只有真正持有控制锁的完成读取者才接受查询，不把STAT排在它等待的226之后。</summary>
    internal bool TryQueueStatus(string? path, out FtpTransferStatusRequest? request)
    {
        lock (_sync)
        {
            request = null;
            if (!_completionReaderActive || _terminal || _abortRequested) return false;
            if (_statusRequests.Count >= 64) throw new InvalidOperationException("同一传输最多排队64个STAT查询。");
            request = new FtpTransferStatusRequest(path);
            _statusRequests.Enqueue(request);
            _statusRequested.TrySetResult();
            return true;
        }
    }

    /// <summary>唯一读取者依次取得STAT查询，并在队列为空时准备新的通知信号。</summary>
    internal bool TryTakeStatus(out FtpTransferStatusRequest? request)
    {
        lock (_sync)
        {
            if (!_statusRequests.TryDequeue(out request)) return false;
            if (_statusRequests.Count == 0) _statusRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    /// <summary>注册数据流的释放回调；必须在把流暴露给调用者之前注册。</summary>
    internal Task RegisterRelease(Func<Task> release)
    {
        lock (_sync)
        {
            _releaseOwner = release;
            return _abort.Task.IsCompletedSuccessfully
                ? _ownerReleaseTask ??= ReleaseOwnerAsync(release) : Task.CompletedTask;
        }
    }

    /// <summary>先公布同步成功，再让独占流归还会话锁；复制事务会在取消后自行归还。</summary>
    internal async Task CompleteAbortAsync(FtpAbortResult result, bool releaseOwner = true)
    {
        _abort.TrySetResult(result);
        Task release;
        lock (_sync)
        {
            _terminal = true;
            while (_statusRequests.TryDequeue(out var status)) status.Cancel();
            release = !releaseOwner || _releaseOwner is null ? Task.CompletedTask : _ownerReleaseTask ??= ReleaseOwnerAsync(_releaseOwner);
        }
        await release.ConfigureAwait(false);
    }

    /// <summary>离开状态短锁后再调用释放回调，避免回调检查同一事务时形成锁顺序依赖。</summary>
    private static async Task ReleaseOwnerAsync(Func<Task> release)
    {
        await Task.Yield();
        await release().ConfigureAwait(false);
    }

    /// <summary>ABOR 未能恢复同步时让所有等待者观察同一个失败。</summary>
    internal void FailAbort(Exception exception)
    {
        lock (_sync)
        {
            _terminal = true;
            while (_statusRequests.TryDequeue(out var status)) status.Fail(exception);
        }
        _abort.TrySetException(exception);
        _ready.TrySetResult();
    }

    /// <summary>连接被外部关闭时取消 I/O 并唤醒所有等待者。</summary>
    internal void Close()
    {
        lock (_sync)
        {
            _terminal = true;
            while (_statusRequests.TryDequeue(out var status)) status.Cancel();
        }
        CancelData();
        _ready.TrySetResult();
        if (AbortRequested) _abort.TrySetException(new IOException("FTP 控制会话在 ABOR 完成前关闭。"));
        else _abort.TrySetCanceled();
    }

    /// <summary>释放本事务拥有的取消注册，不释放客户端的控制锁或会话锁。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _dataCancellation.Dispose();
    }
}

/// <summary>交给唯一控制读取者处理的一次STAT查询，调用者取消等待不会取消已经发出的协议事务。</summary>
internal sealed class FtpTransferStatusRequest
{
    /// <summary>查询完成结果。</summary>
    private readonly TaskCompletionSource<FtpReply> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>创建一次可选路径的STAT请求。</summary>
    internal FtpTransferStatusRequest(string? path) => Path = path;

    /// <summary>查询路径；为空表示整个会话的状态。</summary>
    internal string? Path { get; }

    /// <summary>由唯一控制读取者提供的结果任务。</summary>
    internal Task<FtpReply> Reply => _reply.Task;

    /// <summary>公布完整的STAT最终回复。</summary>
    internal void Complete(FtpReply reply) => _reply.TrySetResult(reply);

    /// <summary>公布控制事务错误。</summary>
    internal void Fail(Exception exception) => _reply.TrySetException(exception);

    /// <summary>传输或连接已结束时取消尚未发出的查询。</summary>
    internal void Cancel() => _reply.TrySetCanceled();
}