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

namespace MonsterSyncDrive.FTP;

/// <summary>唯一命名上传及不透明 REST 检查点传输，所有成功结果都以服务器最终完成回复为准。</summary>
public sealed partial class FtpClient
{
    /// <summary>让服务器用 STOU 创建唯一名称的文件。名称提示可为空，真正路径从 125/150 FILE 回复获得。</summary>
    public Task<FtpUniqueUploadResult> UploadUniqueAsync(Stream source, string? nameHint = null,
        IProgress<FtpTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("源流必须可读。", nameof(source));
        if (nameHint is not null) FtpPath.Validate(nameHint);
        return RunAsync(async token =>
        {
            var stream = await StartDataAsync("STOU", nameHint, 0, token).ConfigureAwait(false);
            var preliminary = _lastDataPreliminaryReply!;
            var name = ParseUniqueName(preliminary);
            var (transferred, completion) = await CopyUploadDataAsync(name, source, stream, 0, progress, token).ConfigureAwait(false);
            return new FtpUniqueUploadResult
            { RemotePath = name, BytesTransferred = transferred, PreliminaryReply = preliminary, CompletionReply = completion };
        }, cancellationToken);
    }

    /// <summary>从本地文件执行唯一命名上传，不覆盖服务器中已经存在的同名文件。</summary>
    public async Task<FtpUniqueUploadResult> UploadUniqueFileAsync(string localPath, string? nameHint = null,
        IProgress<FtpTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, _options.BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await UploadUniqueAsync(file, nameHint, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>复制内存后以服务器生成的唯一名称上传。</summary>
    public async Task<FtpUniqueUploadResult> UploadUniqueBytesAsync(ReadOnlyMemory<byte> bytes, string? nameHint = null,
        CancellationToken cancellationToken = default)
    {
        using var source = new MemoryStream(bytes.ToArray(), writable: false);
        return await UploadUniqueAsync(source, nameHint, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>使用服务器提供的不透明 REST 标记打开下载，不将标记解释为本地文件偏移。</summary>
    public Task<FtpTransferStream> OpenReadAsync(string remotePath, string restartMarker, CancellationToken cancellationToken = default)
    {
        ValidateOpaqueRestartMarker(restartMarker);
        return OpenTransferAsync("RETR", remotePath, 0, canRead: true, cancellationToken, restartMarker);
    }

    /// <summary>使用服务器提供的不透明 REST 标记打开上传；源数据必须从调用者保存的本地检查点开始。</summary>
    public Task<FtpTransferStream> OpenWriteAsync(string remotePath, string restartMarker, CancellationToken cancellationToken = default)
    {
        ValidateOpaqueRestartMarker(restartMarker);
        return OpenTransferAsync("STOR", remotePath, 0, canRead: false, cancellationToken, restartMarker);
    }

    /// <summary>从服务器检查点下载到目标流当前位置，不定位或关闭调用者目标流。</summary>
    public Task<long> DownloadFromMarkerAsync(string remotePath, Stream destination, string restartMarker,
        CancellationToken cancellationToken = default)
    {
        FtpPath.Validate(remotePath);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("目标流必须可写。", nameof(destination));
        ValidateOpaqueRestartMarker(restartMarker);
        return RunAsync(token => CopyDownloadAsync(remotePath, destination, 0, null, null, null, token, restartMarker), cancellationToken);
    }

    /// <summary>从调用者源流当前位置上传到服务器检查点，不定位或关闭源流。</summary>
    public Task<long> UploadFromMarkerAsync(string remotePath, Stream source, string restartMarker,
        CancellationToken cancellationToken = default)
    {
        FtpPath.Validate(remotePath);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("源流必须可读。", nameof(source));
        ValidateOpaqueRestartMarker(restartMarker);
        return RunAsync(token => CopyUploadAsync(remotePath, source, 0, false, null, token, restartMarker), cancellationToken);
    }

    /// <summary>解析 RFC1123 明确定义的 FILE: 唯一路径，不猜测自然语言回复中的文件名。</summary>
    private static string ParseUniqueName(FtpReply preliminary)
    {
        var message = preliminary.Message;
        if (!message.StartsWith("FILE: ", StringComparison.OrdinalIgnoreCase) || message.Length <= 6)
            throw new FtpProtocolException("STOU 预备回复没有包含 RFC1123 规定的 FILE: 唯一路径。");
        var name = message[6..];
        FtpPath.Validate(name);
        return name;
    }

    /// <summary>验证 RFC959 重启标记，不允许空格、控制字符或非可打印 ASCII 字符。</summary>
    private static void ValidateOpaqueRestartMarker(string marker)
    {
        ArgumentException.ThrowIfNullOrEmpty(marker);
        if (marker.Any(static character => character is < '!' or > '~'))
            throw new ArgumentException("REST 检查点必须为不含空格的可打印 ASCII 标记。", nameof(marker));
    }
}