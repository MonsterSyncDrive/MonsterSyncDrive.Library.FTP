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
using System.Diagnostics;

namespace MonsterSyncDrive.FTP;

/// <summary>二进制文件传输、断点续传、追加、进度与可独占会话的流。</summary>
public sealed partial class FtpClient
{
    /// <summary>下载到调用者提供的可写流；在当前位置写入，不定位或关闭目标流。返回本次传输字节数。</summary>
    /// <param name="remotePath">原始远程路径。</param>
    /// <param name="destination">可写目标流；REST 偏移与目标流位置由调用者分别管理。</param>
    /// <param name="restartOffset">远程文件的二进制断点偏移，零表示从头下载。</param>
    /// <param name="progress">可选进度接收器；回调抛异常会中断传输并关闭会话。</param>
    /// <param name="cancellationToken">取消排队或传输；已开始的传输会关闭会话。</param>
    public Task<long> DownloadAsync(string remotePath, Stream destination, long restartOffset = 0,
        IProgress<FtpTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        FtpPath.Validate(remotePath);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("下载目标流必须可写。", nameof(destination));
        ArgumentOutOfRangeException.ThrowIfNegative(restartOffset);
        return RunAsync(token => CopyDownloadAsync(remotePath, destination, restartOffset, null, null, progress, token), cancellationToken);
    }

    /// <summary>上传调用者源流中当前位置之后的数据；不定位或关闭源流。append 使用 APPE，不允许同时指定 REST 偏移。</summary>
    public Task<long> UploadAsync(string remotePath, Stream source, long restartOffset = 0, bool append = false,
        IProgress<FtpTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        FtpPath.Validate(remotePath);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("上传源流必须可读。", nameof(source));
        ArgumentOutOfRangeException.ThrowIfNegative(restartOffset);
        if (append && restartOffset != 0) throw new ArgumentException("APPE 不能与 REST 断点混用。", nameof(restartOffset));
        return RunAsync(token => CopyUploadAsync(remotePath, source, restartOffset, append, progress, token), cancellationToken);
    }

    /// <summary>
    /// 下载到本地文件。resume 为 true 时保留原文件并从末尾续传，且检查远程大小；本地较大时拒绝。
    /// 普通覆盖下载打开文件时即截断旧内容；失败可能留下部分文件，不执行隐式重试或原子替换。
    /// </summary>
    public Task<long> DownloadFileAsync(string remotePath, string localPath, bool resume = false, bool overwrite = true,
        IProgress<FtpTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        FtpPath.Validate(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        return RunAsync(async token =>
        {
            // 在连接事务内才打开本地文件，排队取消不会提前截断文件。
            var mode = resume ? FileMode.OpenOrCreate : overwrite ? FileMode.Create : FileMode.CreateNew;
            await using var file = new FileStream(localPath, mode, FileAccess.Write, FileShare.Read, _options.BufferSize, FileOptions.Asynchronous);
            var offset = resume ? file.Length : 0;
            if (resume) ValidateByteRestart(Math.Max(1, offset));
            long? total = resume ? await GetSizeCoreAsync(remotePath, token).ConfigureAwait(false) : null;
            if (total.HasValue && offset > total.Value) throw new IOException("本地文件大于远程文件，无法续传。");
            file.Position = offset;
            if (resume && offset == total)
            {
                Report(progress, remotePath, 0, offset, total, TimeSpan.Zero);
                return 0L;
            }
            return await CopyDownloadAsync(remotePath, file, offset, total, null, progress, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>
    /// 上传本地文件。Resume 必须能通过 SIZE 确认远程文件大小；远程不存在时请明确使用 Overwrite 创建。
    /// 续传仅验证长度，不证明已有前缀一致；重要数据可在传输后比较 GetChecksumAsync 的结果。
    /// </summary>
    public Task<long> UploadFileAsync(string localPath, string remotePath, FtpUploadMode mode = FtpUploadMode.Overwrite,
        IProgress<FtpTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        FtpPath.Validate(remotePath);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return RunAsync(async token =>
        {
            await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, _options.BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var offset = mode == FtpUploadMode.Resume ? await GetSizeCoreAsync(remotePath, token).ConfigureAwait(false) : 0;
            if (mode == FtpUploadMode.Resume) ValidateByteRestart(Math.Max(1, offset));
            if (offset > file.Length) throw new IOException("远程文件大于本地源文件，无法续传。");
            file.Position = offset;
            if (mode == FtpUploadMode.Resume && offset == file.Length)
            {
                Report(progress, remotePath, 0, offset, file.Length, TimeSpan.Zero);
                return 0L;
            }
            return await CopyUploadAsync(remotePath, file, offset, mode == FtpUploadMode.Append, progress, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>下载到有界内存，默认最多 64 MiB。超限时中断并关闭会话，避免不可信服务器耗尽内存。</summary>
    public Task<byte[]> DownloadBytesAsync(string remotePath, int maxBytes = 67108864, CancellationToken cancellationToken = default)
    {
        FtpPath.Validate(remotePath);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        return RunAsync(async token =>
        {
            using var memory = new MemoryStream();
            await CopyDownloadAsync(remotePath, memory, 0, null, maxBytes, null, token).ConfigureAwait(false);
            return memory.ToArray();
        }, cancellationToken);
    }

    /// <summary>上传一段内存的副本。调用者随后修改原始缓冲区不会改变待发送的数据。</summary>
    public async Task<long> UploadBytesAsync(string remotePath, ReadOnlyMemory<byte> bytes, bool append = false,
        CancellationToken cancellationToken = default)
    {
        using var source = new MemoryStream(bytes.ToArray(), writable: false);
        return await UploadAsync(remotePath, source, append: append, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 打开只读数据流，在读到 EOF 时校验服务器最终回复并归还会话。必须释放；提前关闭会中断并关闭会话。
    /// 返回流的 Length/Position/Seek 不可用，断点只在打开时指定。
    /// </summary>
    public Task<FtpTransferStream> OpenReadAsync(string remotePath, long restartOffset = 0, CancellationToken cancellationToken = default) =>
        OpenTransferAsync("RETR", remotePath, restartOffset, canRead: true, cancellationToken);

    /// <summary>打开只写数据流；CompleteAsync 或 DisposeAsync 发送 EOF、校验最终回复并归还会话。</summary>
    public Task<FtpTransferStream> OpenWriteAsync(string remotePath, long restartOffset = 0, bool append = false,
        CancellationToken cancellationToken = default)
    {
        if (append && restartOffset != 0) throw new ArgumentException("追加与断点不能同时使用。", nameof(restartOffset));
        return OpenTransferAsync(append ? "APPE" : "STOR", remotePath, restartOffset, canRead: false, cancellationToken);
    }

    /// <summary>下载复制循环，每次网络或目标流写入分别受空闲超时限制，字节完成后仍须读取 226/250。</summary>
    private async Task<long> CopyDownloadAsync(string path, Stream destination, long offset, long? total, long? maximum,
        IProgress<FtpTransferProgress>? progress, CancellationToken cancellationToken, string? restartMarker = null)
    {
        var stream = await StartDataAsync("RETR", path, offset, cancellationToken, restartMarker).ConfigureAwait(false);
        var dataToken = GetActiveTransferToken(cancellationToken);
        var buffer = new byte[_options.BufferSize];
        var timer = Stopwatch.StartNew();
        long transferred = 0;
        Report(progress, path, 0, offset, total, timer.Elapsed);
        while (true)
        {
            var count = await FtpTimeout.RunAsync(token => stream.ReadAsync(buffer, token).AsTask(),
                _options.DataTimeout, dataToken, "下载读取").ConfigureAwait(false);
            if (count == 0) break;
            if (maximum.HasValue && transferred + count > maximum.Value) throw new IOException("下载超过允许的内存字节数。");
            await FtpTimeout.RunAsync(token => destination.WriteAsync(buffer.AsMemory(0, count), token).AsTask(),
                _options.DataTimeout, dataToken, "下载写入").ConfigureAwait(false);
            transferred = checked(transferred + count);
            Report(progress, path, transferred, offset, total, timer.Elapsed);
        }
        if (total.HasValue && offset + transferred != total.Value) throw new FtpProtocolException("下载长度与已查询的远程文件大小不一致。");
        await CompleteDataAsync(cancellationToken).ConfigureAwait(false);
        Report(progress, path, transferred, offset, total, timer.Elapsed);
        return transferred;
    }

    /// <summary>上传复制循环；源流完全耗尽后关闭数据流，以 EOF 通知服务器完成写入。</summary>
    private async Task<long> CopyUploadAsync(string path, Stream source, long offset, bool append,
        IProgress<FtpTransferProgress>? progress, CancellationToken cancellationToken, string? restartMarker = null)
    {
        var stream = await StartDataAsync(append ? "APPE" : "STOR", path, offset, cancellationToken, restartMarker).ConfigureAwait(false);
        var result = await CopyUploadDataAsync(path, source, stream, offset, progress, cancellationToken).ConfigureAwait(false);
        return result.BytesTransferred;
    }

    /// <summary>普通、检查点和唯一命名上传共用复制、进度、空闲超时与最终回复校验。</summary>
    private async Task<(long BytesTransferred, FtpReply CompletionReply)> CopyUploadDataAsync(string path, Stream source,
        Stream destination, long offset, IProgress<FtpTransferProgress>? progress, CancellationToken cancellationToken)
    {
        long? total = source.CanSeek ? checked(source.Length - source.Position + offset) : null;
        var dataToken = GetActiveTransferToken(cancellationToken);
        var buffer = new byte[_options.BufferSize];
        var timer = Stopwatch.StartNew();
        long transferred = 0;
        Report(progress, path, 0, offset, total, timer.Elapsed);
        while (true)
        {
            var count = await FtpTimeout.RunAsync(token => source.ReadAsync(buffer, token).AsTask(),
                _options.DataTimeout, dataToken, "上传源流读取").ConfigureAwait(false);
            if (count == 0) break;
            await FtpTimeout.RunAsync(token => destination.WriteAsync(buffer.AsMemory(0, count), token).AsTask(),
                _options.DataTimeout, dataToken, "上传写入").ConfigureAwait(false);
            transferred = checked(transferred + count);
            Report(progress, path, transferred, offset, total, timer.Elapsed);
        }
        var completion = await CompleteDataAsync(cancellationToken).ConfigureAwait(false);
        Report(progress, path, transferred, offset, total, timer.Elapsed);
        return (transferred, completion);
    }

    /// <summary>创建携带独占会话锁的数据流。锁的释放责任从此方法转移到完成回调，确保不重复归还。</summary>
    private async Task<FtpTransferStream> OpenTransferAsync(string verb, string path, long offset, bool canRead,
        CancellationToken cancellationToken, string? restartMarker = null)
    {
        FtpPath.Validate(path);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var linked = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = await StartDataAsync(verb, path, offset, linked.Token, restartMarker).ConfigureAwait(false);
            var transfer = _activeTransfer!;
            var result = new FtpTransferStream(stream, canRead, _options.DataTimeout, GetActiveTransferToken(linked.Token), async success =>
            {
                try
                {
                    if (transfer.AbortRequested)
                        throw new FtpTransferAbortedException(await transfer.AbortCompletion.ConfigureAwait(false));
                    if (success) { linked.Token.ThrowIfCancellationRequested(); await CompleteDataAsync(linked.Token).ConfigureAwait(false); }
                    else CloseConnection();
                }
                catch (FtpTransferAbortedException) when (IsConnected) { throw; }
                catch (Exception exception)
                {
                    var normalized = await HandleTransferFailureAsync(exception).ConfigureAwait(false);
                    if (normalized is not FtpTransferAbortedException || !IsConnected) CloseConnection();
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(normalized).Throw();
                }
                finally { linked.Dispose(); _sessionGate.Release(); }
            }, () => AbortAsync());
            await RegisterActiveTransferRelease(async () =>
            {
                try { await result.ReleaseAfterAbortAsync().ConfigureAwait(false); }
                catch (FtpTransferAbortedException) { }
            }).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            var normalized = await HandleTransferFailureAsync(exception).ConfigureAwait(false);
            if (normalized is not FtpTransferAbortedException &&
                (normalized is not FtpCommandException commandException || !CanKeepConnection(commandException)))
                CloseConnection();
            linked.Dispose();
            _sessionGate.Release();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(normalized).Throw();
            throw;
        }
    }

    /// <summary>在进度快照中明确区分本次复制量与断点，保持原始路径供调用者关联操作。</summary>
    private static void Report(IProgress<FtpTransferProgress>? progress, string path, long transferred, long offset, long? total, TimeSpan elapsed) =>
        progress?.Report(new FtpTransferProgress
        {
            RemotePath = path,
            BytesTransferred = transferred,
            RestartOffset = offset,
            TotalBytes = total,
            Elapsed = elapsed
        });
}