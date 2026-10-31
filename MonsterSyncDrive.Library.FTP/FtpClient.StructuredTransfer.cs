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

/// <summary>记录与页结构专用传输，显式保留记录边界、页索引和页头，不把结构化文件扁平化。</summary>
public sealed partial class FtpClient
{
    /// <summary>上传任意二进制记录，包括空记录及含换行/0xff 的记录；自动选择 STRU R。</summary>
    public Task UploadRecordsAsync(string remotePath, IEnumerable<ReadOnlyMemory<byte>> records,
        string? restartMarker = null, CancellationToken cancellationToken = default)
    {
        _ = FtpPath.Validate(remotePath);
        ArgumentNullException.ThrowIfNull(records);
        if (restartMarker is not null)
            ValidateOpaqueRestartMarker(restartMarker);
        return RunAsync(async token =>
        {
            await ApplyTransferSettingsCoreAsync(_transferSettings with
            {
                Structure = FtpFileStructure.Record
            }, token).ConfigureAwait(false);
            Stream transport = await StartDataAsync("STOR", remotePath, 0, token, restartMarker, rawData: true).ConfigureAwait(false);
            await FtpDataCodec.WriteRecordsAsync(transport, records, _transferSettings, leaveOpen: true,
                GetActiveTransferToken(token)).ConfigureAwait(false);
            _ = await CompleteDataAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>下载明确记录边界的文件；内存总量及记录数量受独立预算限制，默认最多 64 MiB。</summary>
    public Task<IReadOnlyList<byte[]>> DownloadRecordsAsync(string remotePath, string? restartMarker = null,
        int maxRecords = 65536, long maxBytes = 67108864, CancellationToken cancellationToken = default)
    {
        _ = FtpPath.Validate(remotePath);
        if (restartMarker is not null)
            ValidateOpaqueRestartMarker(restartMarker);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecords);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        return RunAsync(async token =>
        {
            await ApplyTransferSettingsCoreAsync(_transferSettings with
            {
                Structure = FtpFileStructure.Record
            }, token).ConfigureAwait(false);
            Stream transport = await StartDataAsync("RETR", remotePath, 0, token, restartMarker, rawData: true).ConfigureAwait(false);
            List<byte[]> result = new();
            long length = 0;
            await foreach (var record in FtpDataCodec.ReadRecordsAsync(transport, _transferSettings, leaveOpen: true,
                GetActiveTransferToken(token)).ConfigureAwait(false))
            {
                if (result.Count >= maxRecords || record.LongLength > maxBytes - length)
                    throw new FtpProtocolException("记录下载超过数量或总内存预算。");
                length += record.LongLength;
                result.Add(record);
            }
            _ = await CompleteDataAsync(token).ConfigureAwait(false);
            return (IReadOnlyList<byte[]>)result.AsReadOnly();
        }, cancellationToken);
    }

    /// <summary>上传包含独立索引和访问控制头的页文件，自动选择 STRU P；TYPE 必须为 Image 或 Local。</summary>
    public Task UploadPagesAsync(string remotePath, IEnumerable<FtpPage> pages,
        string? restartMarker = null, CancellationToken cancellationToken = default)
    {
        _ = FtpPath.Validate(remotePath);
        ArgumentNullException.ThrowIfNull(pages);
        if (restartMarker is not null)
            ValidateOpaqueRestartMarker(restartMarker);
        return RunAsync(async token =>
        {
            await ApplyTransferSettingsCoreAsync(_transferSettings with
            {
                Structure = FtpFileStructure.Page
            }, token).ConfigureAwait(false);
            Stream transport = await StartDataAsync("STOR", remotePath, 0, token, restartMarker, rawData: true).ConfigureAwait(false);
            await FtpDataCodec.WritePagesAsync(transport, pages, _transferSettings, leaveOpen: true,
                GetActiveTransferToken(token)).ConfigureAwait(false);
            _ = await CompleteDataAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>下载页文件，保留逻辑索引、页类型和扩展头；默认最多 1024 页及 1048576 个逻辑数据值。</summary>
    public Task<IReadOnlyList<FtpPage>> DownloadPagesAsync(string remotePath, string? restartMarker = null,
        int maxPages = 1024, long maxLogicalBytes = 1048576, CancellationToken cancellationToken = default)
    {
        _ = FtpPath.Validate(remotePath);
        if (restartMarker is not null)
            ValidateOpaqueRestartMarker(restartMarker);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPages);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLogicalBytes);
        return RunAsync(async token =>
        {
            await ApplyTransferSettingsCoreAsync(_transferSettings with
            {
                Structure = FtpFileStructure.Page
            }, token).ConfigureAwait(false);
            Stream transport = await StartDataAsync("RETR", remotePath, 0, token, restartMarker, rawData: true).ConfigureAwait(false);
            List<FtpPage> result = new();
            long logicalBytes = 0;
            await foreach (FtpPage? page in FtpDataCodec.ReadPagesAsync(transport, _transferSettings, leaveOpen: true,
                GetActiveTransferToken(token)).ConfigureAwait(false))
            {
                var count = (long)page.Data.Count + page.HeaderExtensions.Count;
                if (result.Count >= maxPages || count > maxLogicalBytes - logicalBytes)
                    throw new FtpProtocolException("页下载超过页数或总逻辑数据预算。");
                logicalBytes += count;
                result.Add(page);
            }
            _ = await CompleteDataAsync(token).ConfigureAwait(false);
            return (IReadOnlyList<FtpPage>)result.AsReadOnly();
        }, cancellationToken);
    }
}