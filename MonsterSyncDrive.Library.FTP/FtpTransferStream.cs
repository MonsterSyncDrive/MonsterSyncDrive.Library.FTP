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

using MonsterSyncDrive.Library.FTP.Protocol;

namespace MonsterSyncDrive.Library.FTP;

/// <summary>
/// 独占 FTP 会话的数据流。单个流只允许一个读写使用者；完成、失败或释放时恰好归还一次会话锁。
/// 下载读到 EOF 才算成功；上传必须调用 CompleteAsync 或释放以校验服务器最终状态。
/// </summary>
public sealed class FtpTransferStream : Stream
{
    /// <summary>底层网络或 TLS 数据流，生命周期由会话完成回调管理。</summary>
    private readonly Stream _stream;

    /// <summary>此实例是否允许读取；否则只允许写入。</summary>
    private readonly bool _readable;

    /// <summary>每次 I/O 的空闲超时。</summary>
    private readonly TimeSpan _timeout;

    /// <summary>创建流时绑定的调用者与客户端生命周期取消标记。</summary>
    private readonly CancellationToken _sessionToken;

    /// <summary>会话完成回调；true 表示读取最终回复，false 表示中断连接。</summary>
    private readonly Func<bool, Task> _finish;

    /// <summary>显式中止委托，使用客户端 ABOR 并保留控制会话。</summary>
    private readonly Func<Task<FtpAbortResult>>? _abort;

    /// <summary>保护完成任务的初始化，防止释放与错误处理重复归还会话锁。</summary>
    private readonly object _finishLock = new();

    /// <summary>已经启动的唯一完成任务，保存最终回复错误以便观察。</summary>
    private Task? _completion;

    /// <summary>是否已读取 EOF。</summary>
    private bool _eof;

    /// <summary>流关闭或完成标记。</summary>
    private int _closed;

    /// <summary>内部创建独占数据流；完成回调取得底层资源和锁的释放责任。</summary>
    internal FtpTransferStream(Stream stream, bool readable, TimeSpan timeout, CancellationToken sessionToken,
        Func<bool, Task> finish, Func<Task<FtpAbortResult>>? abort = null)
    {
        _stream = stream;
        _readable = readable;
        _timeout = timeout;
        _sessionToken = sessionToken;
        _finish = finish;
        _abort = abort;
    }

    /// <summary>流仍打开且方向为下载时为 true。</summary>
    public override bool CanRead => _readable && Volatile.Read(ref _closed) == 0;

    /// <summary>FTP 数据流不支持定位。</summary>
    public override bool CanSeek => false;

    /// <summary>流仍打开且方向为上传时为 true。</summary>
    public override bool CanWrite => !_readable && Volatile.Read(ref _closed) == 0;

    /// <summary>流不提供长度；使用客户端 GetFileSizeAsync 查询。</summary>
    public override long Length => throw new NotSupportedException("FTP 数据流不支持 Length。");

    /// <summary>流不提供位置；需要断点时在打开流时指定 restartOffset。</summary>
    public override long Position
    {
        get => throw new NotSupportedException("FTP 数据流不支持 Position。");
        set => throw new NotSupportedException("FTP 数据流不支持 Position。");
    }

    /// <summary>显式完成上传，或确认已读完的下载；读取未到 EOF 时关闭会话并抛出异常。</summary>
    public async Task CompleteAsync()
    {
        if (_readable && !_eof)
        {
            await FinishAsync(false).ConfigureAwait(false);
            throw new InvalidOperationException("下载流必须读到 EOF 后才能确认成功。");
        }
        await FinishAsync(true).ConfigureAwait(false);
    }

    /// <summary>显式中断数据流，通过 ABOR 同步完成回复并保留控制会话；重复中止不会重复归还锁。</summary>
    public async Task AbortAsync()
    {
        if (Volatile.Read(ref _closed) != 0) return;
        if (_abort is not null) await _abort().ConfigureAwait(false);
        else await FinishAsync(false).ConfigureAwait(false);
    }

    /// <summary>供客户端已完成 ABOR 的释放回调使用，不再递归发送另一个 ABOR。</summary>
    internal Task ReleaseAfterAbortAsync() => FinishAsync(false);

    /// <summary>在块/压缩模式上传流中插入一个可恢复检查点，服务器可用 110 回复返回对应标记。</summary>
    public async Task WriteRestartMarkerAsync(string marker, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        if (_readable || _stream is not FtpDataWriteStream writer)
            throw new NotSupportedException("只有编码后的上传数据流可以写入检查点。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_sessionToken, cancellationToken);
        try
        {
            await FtpTimeout.RunAsync(token => writer.WriteRestartMarkerAsync(marker, token),
                _timeout, linked.Token, "重启检查点写入").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var normalized = await NormalizeAfterErrorAsync(exception).ConfigureAwait(false);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(normalized).Throw();
        }
    }

    /// <summary>下载数据通道最近提供的本地重启检查点；尚未接收或当前模式没有检查点时为空。</summary>
    public FtpRestartMarker? LastRestartMarker => (_stream as FtpDataReadStream)?.LastRestartMarker;

    /// <summary>异步读取。EOF 时先校验服务器最终回复；失败不会被伪装为正常 EOF。</summary>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_readable) throw new NotSupportedException("此 FTP 流只允许写入。");
        if (_eof) { await FinishAsync(true).ConfigureAwait(false); return 0; }
        EnsureOpen();
        if (buffer.Length == 0) return 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_sessionToken, cancellationToken);
        try
        {
            var read = await FtpTimeout.RunAsync(token => _stream.ReadAsync(buffer, token).AsTask(),
                _timeout, linked.Token, "数据流读取").ConfigureAwait(false);
            if (read == 0) { _eof = true; await FinishAsync(true).ConfigureAwait(false); }
            return read;
        }
        catch (Exception exception)
        {
            var normalized = await NormalizeAfterErrorAsync(exception).ConfigureAwait(false);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(normalized).Throw();
            throw;
        }
    }

    /// <summary>异步写入，超时、取消或底层错误时中断会话。</summary>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_readable) throw new NotSupportedException("此 FTP 流只允许读取。");
        EnsureOpen();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_sessionToken, cancellationToken);
        try
        {
            await FtpTimeout.RunAsync(token => _stream.WriteAsync(buffer, token).AsTask(), _timeout, linked.Token, "数据流写入")
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var normalized = await NormalizeAfterErrorAsync(exception).ConfigureAwait(false);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(normalized).Throw();
            throw;
        }
    }

    /// <summary>异步刷新底层上传流；刷新本身不会完成 FTP 事务。</summary>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        EnsureOpen();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_sessionToken, cancellationToken);
        try
        {
            await FtpTimeout.RunAsync(token => _stream.FlushAsync(token), _timeout, linked.Token, "数据流刷新").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var normalized = await NormalizeAfterErrorAsync(exception).ConfigureAwait(false);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(normalized).Throw();
            throw;
        }
    }

    /// <summary>兼容同步 Stream 调用；异步实现不捕获同步上下文。</summary>
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <summary>兼容旧式数组异步读取入口。</summary>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>兼容同步上传入口。</summary>
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <summary>兼容旧式数组异步写入入口。</summary>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>同步刷新，不触发传输完成。</summary>
    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>不支持 Seek，FTP 断点只能在打开数据连接前通过 REST 设置。</summary>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("FTP 数据流不支持 Seek。");

    /// <summary>不支持 SetLength，覆盖由 STOR 命令控制。</summary>
    public override void SetLength(long value) => throw new NotSupportedException("FTP 数据流不支持 SetLength。");

    /// <summary>同步释放并校验上传最终回复；下载提前关闭则中断会话。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { FinishAsync(!_readable || _eof).GetAwaiter().GetResult(); }
            catch (FtpTransferAbortedException) { /* 已通过显式 ABOR 确认并释放，重复释放不重报中止。 */ }
        }
        base.Dispose(disposing);
    }

    /// <summary>异步释放。上传结束错误通过此入口传播，调用者应优先使用 await using。</summary>
    public override async ValueTask DisposeAsync()
    {
        try { await FinishAsync(!_readable || _eof).ConfigureAwait(false); }
        catch (FtpTransferAbortedException) { /* 中止状态由数据操作或 AbortAsync 的结果报告。 */ }
        finally { GC.SuppressFinalize(this); }
    }

    /// <summary>验证流尚未完成；完成后的写操作一律拒绝。</summary>
    private void EnsureOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);

    /// <summary>只启动一次完成事务；之后所有完成/释放调用观察同一个任务。</summary>
    private Task FinishAsync(bool success)
    {
        lock (_finishLock)
        {
            Volatile.Write(ref _closed, 1);
            return _completion ??= _finish(success);
        }
    }

    /// <summary>出现 I/O 错误时保留原始异常；已观察到的完成错误不会被第二次清理异常覆盖。</summary>
    private async Task<Exception> NormalizeAfterErrorAsync(Exception exception)
    {
        try { await FinishAsync(false).ConfigureAwait(false); }
        catch (FtpTransferAbortedException aborted) { return aborted; }
        catch (Exception) { /* 原始 I/O 或最终回复异常由外层重新抛出。 */ }
        return exception;
    }
}