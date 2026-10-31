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
using System.Runtime.CompilerServices;

namespace MonsterSyncDrive.FTP.Protocol;

/// <summary>FTP 数据表示与 MODE/STRU 编码工厂；控制层先完成数据编码器，再关闭原始 TLS/TCP 通道。</summary>
public static partial class FtpDataCodec
{
    /// <summary>建立读取解码器；默认不释放原始网络流，便于控制层完成 TLS 关闭握手。</summary>
    public static FtpDataReadStream CreateReadStream(Stream source, FtpTransferSettings settings, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        if (!source.CanRead) throw new ArgumentException("数据源必须可读。", nameof(source));
        return new FtpDataReadStream(source, settings.Snapshot(), leaveOpen);
    }

    /// <summary>建立写入编码器；必须 CompleteAsync 或释放以写出 EOF/压缩尾部。</summary>
    public static FtpDataWriteStream CreateWriteStream(Stream destination, FtpTransferSettings settings, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(settings);
        if (!destination.CanWrite) throw new ArgumentException("数据目标必须可写。", nameof(destination));
        return new FtpDataWriteStream(destination, settings.Snapshot(), leaveOpen);
    }

    /// <summary>按明确的 EOR 边界读取任意二进制记录，不把记录中的 LF 错当成结构分隔符。</summary>
    public static async IAsyncEnumerable<byte[]> ReadRecordsAsync(Stream source, FtpTransferSettings settings,
        bool leaveOpen = true, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Structure != FtpFileStructure.Record) throw new ArgumentException("记录 API 要求 STRU R。", nameof(settings));
        await using var decoder = CreateReadStream(source, settings, leaveOpen);
        while (await decoder.ReadRecordAsync(cancellationToken).ConfigureAwait(false) is { } record) yield return record;
    }

    /// <summary>编码独立记录；每个记录可以包含 LF、0xff 或空内容，最后自动发送文件结束标记。</summary>
    public static async Task WriteRecordsAsync(Stream destination, IEnumerable<ReadOnlyMemory<byte>> records,
        FtpTransferSettings settings, bool leaveOpen = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Structure != FtpFileStructure.Record) throw new ArgumentException("记录 API 要求 STRU R。", nameof(settings));
        await using var encoder = CreateWriteStream(destination, settings, leaveOpen);
        foreach (var record in records) await encoder.WriteRecordAsync(record, cancellationToken).ConfigureAwait(false);
        await encoder.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }
}