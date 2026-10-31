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

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MonsterSyncDrive.Library.FTP.Protocol;

/// <summary>FTP 数据写入编码器。必须先完成本编码器，再关闭外层控制器持有的 TLS/TCP 数据连接。</summary>
public sealed class FtpDataWriteStream : Stream
{
    /// <summary>调用者提供的原始数据流。</summary>
    private readonly Stream _destination;
    /// <summary>原始流或 MODE Z 的 zlib 压缩器。</summary>
    private readonly Stream _wire;
    /// <summary>此次传输的不可变参数。</summary>
    private readonly FtpTransferSettings _settings;
    /// <summary>释放时是否保留原始流。</summary>
    private readonly bool _leaveOpen;
    /// <summary>MODE C 保留最后一个字节串，让 EOF/EOR 描述符放在该串之前。</summary>
    private readonly List<byte> _compressedPending = new(127);
    /// <summary>跨调用保留的本地文本 CR，区分 CRLF 和单独 CR。</summary>
    private bool _pendingCarriageReturn;
    /// <summary>当前记录是否含有尚未结束的应用数据。</summary>
    private bool _recordHasData;
    /// <summary>当前记录的应用输入字节数。</summary>
    private long _recordLength;
    /// <summary>字节流/显式记录接口选择，防止隐式合并边界。</summary>
    private int _api;
    /// <summary>是否已经发送协议 EOF。</summary>
    private bool _completed;
    /// <summary>是否已中止，释放时不得发送成功 EOF。</summary>
    private bool _aborted;
    /// <summary>包装器是否已释放。</summary>
    private bool _disposed;
    /// <summary>应用 Latin1 到 CP037 的映射，不依赖全局编码注册。</summary>
    private static readonly byte[] EbcdicEncode = CreateEbcdicEncode();

    /// <summary>建立由工厂校验的写入包装器。</summary>
    internal FtpDataWriteStream(Stream destination, FtpTransferSettings settings, bool leaveOpen)
    {
        _destination = destination;
        _settings = settings;
        _leaveOpen = leaveOpen;
        _wire = settings.Mode == FtpTransmissionMode.Deflate
            ? new ZLibStream(destination, settings.CompressionLevel, leaveOpen: true) : destination;
    }

    /// <summary>写入编码器不可读。</summary>
    public override bool CanRead => false;
    /// <summary>数据帧和压缩状态不可定位。</summary>
    public override bool CanSeek => false;
    /// <summary>仍可写入且尚未完成时为 true。</summary>
    public override bool CanWrite => !_disposed && !_completed;
    /// <summary>网络表示的最终长度不可预先查询。</summary>
    public override long Length => throw new NotSupportedException();
    /// <summary>网络数据流不可定位。</summary>
    public override long Position
    {
        get => throw new NotSupportedException(); set => throw new NotSupportedException();
    }

    /// <summary>编码应用数据；普通 STRU R 字节流的 LF 明确表示一条记录结束。</summary>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (_api == 2)
            throw new InvalidOperationException("不能混用普通字节写入和显式记录写入。");
        _api = 1;
        if (_settings.Structure != FtpFileStructure.Record)
        {
            await WriteRepresentationAsync(buffer, cancellationToken).ConfigureAwait(false);
            return;
        }
        int position = 0;
        while (position < buffer.Length)
        {
            ReadOnlyMemory<byte> remainder = buffer[position..];
            int newline = remainder.Span.IndexOf((byte)10);
            int count = newline < 0 ? remainder.Length : newline;
            if (count > 0)
            {
                if (count > _settings.MaxRecordBytes - _recordLength)
                    throw new FtpProtocolException("FTP 记录超过配置的长度限制。");
                _recordLength += count;
                _recordHasData = true;
                await WriteRepresentationAsync(remainder[..count], cancellationToken).ConfigureAwait(false);
            }
            position += count;
            if (newline >= 0)
            {
                // 普通文本流中的 CRLF 是一个记录分隔，而不是把 CR 存进记录数据。
                if (_settings.DataType is FtpDataType.Ascii or FtpDataType.Ebcdic)
                    _pendingCarriageReturn = false;
                await EndRecordAsync(cancellationToken).ConfigureAwait(false);
                position++;
            }
        }
    }

    /// <summary>写入一条完整记录；记录中的 LF 不会被当作结构边界，空记录也明确发送 EOR。</summary>
    public async Task WriteRecordAsync(ReadOnlyMemory<byte> record, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (_settings.Structure != FtpFileStructure.Record)
            throw new InvalidOperationException("显式记录写入要求 STRU R。");
        if (_api == 1)
            throw new InvalidOperationException("不能混用普通字节写入和显式记录写入。");
        if (record.Length > _settings.MaxRecordBytes)
            throw new ArgumentException("记录超过配置的长度限制。", nameof(record));
        _api = 2;
        _recordHasData = true;
        await WriteRepresentationAsync(record, cancellationToken).ConfigureAwait(false);
        await FlushCarriageReturnAsync(cancellationToken).ConfigureAwait(false);
        await EndRecordAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>在 MODE B/C 数据中写入发送端定义的可打印检查点；标记不占用应用数据。</summary>
    public async Task WriteRestartMarkerAsync(string marker, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        ArgumentException.ThrowIfNullOrEmpty(marker);
        if (_settings.Mode is not (FtpTransmissionMode.Block or FtpTransmissionMode.Compressed))
            throw new InvalidOperationException("数据内重启标记只适用于 MODE B 或 C。");
        if (marker.Length > (_settings.Mode == FtpTransmissionMode.Compressed ? 127 : 65535) ||
            marker.Any(static character => character is < '!' or > '~'))
            throw new ArgumentException("重启标记必须为有界、无空格的可打印 ASCII 字符。", nameof(marker));
        if (_pendingCarriageReturn)
            throw new InvalidOperationException("检查点不能切断尚未确定的 CRLF；请先写入后续文本以完成表示状态。");
        if (_settings.Mode == FtpTransmissionMode.Compressed)
        {
            await FlushCompressedAsync(0, cancellationToken).ConfigureAwait(false);
            await EmitCompressedAsync(Encoding.ASCII.GetBytes(marker), 0x10, cancellationToken).ConfigureAwait(false);
        }
        else
            await EmitBlockAsync(Encoding.ASCII.GetBytes(marker), 0x10, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>发送显式 EOF、压缩尾部并刷新；幂等完成不会重复写入终止标记。</summary>
    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            return;
        _completed = true;
        try
        {
            await FlushCarriageReturnAsync(cancellationToken).ConfigureAwait(false);
            byte finalFlags = 0x40;
            if (_settings.Structure == FtpFileStructure.Record && _recordHasData)
            {
                finalFlags |= 0x80;
                _recordHasData = false;
            }
            if (_settings.Mode == FtpTransmissionMode.Block)
                await EmitBlockAsync(ReadOnlyMemory<byte>.Empty, finalFlags, cancellationToken).ConfigureAwait(false);
            else if (_settings.Mode == FtpTransmissionMode.Compressed)
                await FlushCompressedAsync(finalFlags, cancellationToken).ConfigureAwait(false);
            else if (_settings.Structure == FtpFileStructure.Record)
                await _wire.WriteAsync(new byte[] { 255, (byte)(((finalFlags & 0x80) != 0 ? 1 : 0) | 2) }, cancellationToken).ConfigureAwait(false);

            if (!ReferenceEquals(_wire, _destination))
                await _wire.DisposeAsync().ConfigureAwait(false);
            else
                await _wire.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 完成失败不能在后续 Dispose 时补发成功 EOF；仍需清理 zlib 的本机状态。
            _aborted = true;
            throw;
        }
    }

    /// <summary>标记中止；调用者应先关闭原始数据套接字，之后释放时不会再宣告成功 EOF。</summary>
    public void Abort()
    {
        _aborted = true;
        _completed = true;
    }

    /// <summary>将应用端 Latin1/ASCII 文本转换为 NVT ASCII 或 EBCDIC，二进制/本地位流保持不变。</summary>
    private async Task WriteRepresentationAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_settings.PreserveRepresentation || _settings.DataType is FtpDataType.Image or FtpDataType.Local)
        {
            await WriteEncodedAsync(buffer, cancellationToken).ConfigureAwait(false);
            return;
        }
        var output = new ArrayBufferWriter<byte>();
        foreach (byte value in buffer.Span)
        {
            if (_settings.DataType == FtpDataType.Ascii && value > 127)
                throw new ArgumentException("TYPE A 的应用输入必须是七位 ASCII。", nameof(buffer));
            if (_pendingCarriageReturn)
            {
                _pendingCarriageReturn = false;
                if (value == 10)
                {
                    AppendNewline(output);
                    continue;
                }
                AppendCarriageReturn(output);
            }
            if (value == 13)
                _pendingCarriageReturn = true;
            else if (value == 10)
                AppendNewline(output);
            else
                Append(output, _settings.DataType == FtpDataType.Ebcdic ? EbcdicEncode[value] : value);
        }
        await WriteEncodedAsync(output.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>输出一个规范化文本换行；ASCII 使用 CRLF，EBCDIC 使用 NL 0x15。</summary>
    private void AppendNewline(ArrayBufferWriter<byte> output)
    {
        if (_settings.DataType == FtpDataType.Ascii)
        {
            Append(output, 13);
            Append(output, 10);
        }
        else
            Append(output, 0x15);
    }

    /// <summary>输出单独 CR；ASCII 必须使用 CR NUL，EBCDIC 使用 CR 字符。</summary>
    private void AppendCarriageReturn(ArrayBufferWriter<byte> output)
    {
        Append(output, _settings.DataType == FtpDataType.Ebcdic ? EbcdicEncode[13] : (byte)13);
        if (_settings.DataType == FtpDataType.Ascii)
            Append(output, 0);
    }

    /// <summary>在记录、检查点或 EOF 之前明确完成跨调用保留的 CR。</summary>
    private async Task FlushCarriageReturnAsync(CancellationToken cancellationToken)
    {
        if (!_pendingCarriageReturn)
            return;
        _pendingCarriageReturn = false;
        var output = new ArrayBufferWriter<byte>();
        AppendCarriageReturn(output);
        await WriteEncodedAsync(output.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把表示字节写入所选 MODE，记录结构在流/Z 模式中转义 0xff。</summary>
    private async Task WriteEncodedAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (data.Length == 0)
            return;
        if (_settings.Mode == FtpTransmissionMode.Block)
        {
            for (int offset = 0; offset < data.Length; offset += 65535)
                await EmitBlockAsync(data.Slice(offset, Math.Min(65535, data.Length - offset)), 0, cancellationToken).ConfigureAwait(false);
        }
        else if (_settings.Mode == FtpTransmissionMode.Compressed)
        {
            for (int index = 0; index < data.Length; index++)
            {
                if (_compressedPending.Count == 127)
                    await FlushCompressedAsync(0, cancellationToken).ConfigureAwait(false);
                _compressedPending.Add(data.Span[index]);
            }
        }
        else if (_settings.Structure == FtpFileStructure.Record)
        {
            var output = new ArrayBufferWriter<byte>();
            foreach (byte value in data.Span)
            {
                Append(output, value);
                if (value == 255)
                    Append(output, value);
            }
            await _wire.WriteAsync(output.WrittenMemory, cancellationToken).ConfigureAwait(false);
        }
        else
            await _wire.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>结束一条记录，不关闭文件；MODE C 的 EOR 放在其最后一个字节串之前。</summary>
    private async Task EndRecordAsync(CancellationToken cancellationToken)
    {
        if (_settings.Mode == FtpTransmissionMode.Block)
            await EmitBlockAsync(ReadOnlyMemory<byte>.Empty, 0x80, cancellationToken).ConfigureAwait(false);
        else if (_settings.Mode == FtpTransmissionMode.Compressed)
            await FlushCompressedAsync(0x80, cancellationToken).ConfigureAwait(false);
        else
            await _wire.WriteAsync(new byte[] { 255, 1 }, cancellationToken).ConfigureAwait(false);
        _recordLength = 0;
        _recordHasData = false;
    }

    /// <summary>写出有界块；长度字段是大端的 16 位传输字节计数，不是逻辑 TYPE L 字长。</summary>
    private async Task EmitBlockAsync(ReadOnlyMemory<byte> data, byte descriptor, CancellationToken cancellationToken)
    {
        byte[] header = new byte[3];
        header[0] = descriptor;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(1), checked((ushort)data.Length));
        await _wire.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!data.IsEmpty)
            await _wire.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>提交最后保留的 MODE C 单元；空结构单元只发出转义描述符。</summary>
    private async Task FlushCompressedAsync(byte descriptor, CancellationToken cancellationToken)
    {
        if (_compressedPending.Count == 0 && descriptor == 0)
            return;
        byte[] bytes = _compressedPending.ToArray();
        _compressedPending.Clear();
        await EmitCompressedAsync(bytes, descriptor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>编码 MODE C。带描述符的最后一个单元必须是一个正计数字节串，不能拆成多个有歧义的子串。</summary>
    private async Task EmitCompressedAsync(ReadOnlyMemory<byte> data, byte descriptor, CancellationToken cancellationToken)
    {
        var output = new ArrayBufferWriter<byte>();
        if (descriptor != 0)
        {
            Append(output, 0);
            Append(output, descriptor);
        }
        if (descriptor != 0)
        {
            if (!data.IsEmpty)
            {
                Append(output, checked((byte)data.Length));
                Append(output, data.Span);
            }
        }
        else
        {
            int position = 0;
            byte filler = _settings.DataType switch
            {
                FtpDataType.Ascii => 32,
                FtpDataType.Ebcdic => 64,
                _ => 0
            };
            while (position < data.Length)
            {
                int run = CountRun(data.Span, position, 63);
                if (data.Span[position] == filler)
                {
                    Append(output, (byte)(0xc0 | run));
                    position += run;
                }
                else if (run >= 3)
                {
                    Append(output, (byte)(0x80 | run));
                    Append(output, data.Span[position]);
                    position += run;
                }
                else
                {
                    int start = position++;
                    while (position < data.Length && position - start < 127 && data.Span[position] != filler &&
                        CountRun(data.Span, position, 63) < 3)
                        position++;
                    Append(output, (byte)(position - start));
                    Append(output, data.Span[start..position]);
                }
            }
        }
        await _wire.WriteAsync(output.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>计算一个有上限的重复段长度，避免六位计数字段溢出。</summary>
    private static int CountRun(ReadOnlySpan<byte> data, int start, int maximum)
    {
        int end = start + 1;
        while (end < data.Length && end - start < maximum && data[end] == data[start])
            end++;
        return end - start;
    }

    /// <summary>追加一个传输字节。</summary>
    private static void Append(ArrayBufferWriter<byte> output, byte value)
    {
        output.GetSpan(1)[0] = value;
        output.Advance(1);
    }
    /// <summary>追加一段已表示的数据。</summary>
    private static void Append(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> value)
    {
        value.CopyTo(output.GetSpan(value.Length));
        output.Advance(value.Length);
    }
    /// <summary>验证本编码器仍可写入。</summary>
    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed || _completed, this);
    }

    /// <summary>创建 BCL CP037 编码映射，不向全进程注册编码提供器。</summary>
    private static byte[] CreateEbcdicEncode()
    {
        Encoding encoding = CodePagesEncodingProvider.Instance.GetEncoding(37) ?? throw new NotSupportedException("BCL 不提供 EBCDIC CP037。");
        return encoding.GetBytes(Enumerable.Range(0, 256).Select(static value => (char)value).ToArray());
    }

    /// <summary>异步刷新当前已确定的数据；不结束文件或记录，不丢弃待配对的 CR。</summary>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        EnsureWritable();
        if (_settings.Mode == FtpTransmissionMode.Compressed)
            await FlushCompressedAsync(0, cancellationToken).ConfigureAwait(false);
        await _wire.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    /// <summary>同步刷新包装器。</summary>
    public override void Flush()
    {
        FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>数组形式异步写入适配。</summary>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>同步写入适配，不捕获同步上下文。</summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>写入包装器不可读取。</summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    /// <summary>分帧和压缩状态不可定位。</summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    /// <summary>网络数据不能通过 SetLength 修改。</summary>
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <summary>同步释放时完成文件；中止情况下只清理压缩资源，不发送成功的协议 EOF。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            try
            {
                if (!_aborted)
                    CompleteAsync().GetAwaiter().GetResult();
                else if (!ReferenceEquals(_wire, _destination))
                    try
                    {
                        _wire.Dispose();
                    }
                    catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            }
            finally
            {
                _disposed = true;
                if (!_leaveOpen)
                    _destination.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    /// <summary>异步完成 EOF 与 zlib 尾部，再根据 leaveOpen 清理原始流。</summary>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        try
        {
            if (!_aborted)
                await CompleteAsync().ConfigureAwait(false);
            else if (!ReferenceEquals(_wire, _destination))
                try
                {
                    await _wire.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        }
        finally
        {
            _disposed = true;
            if (!_leaveOpen)
                await _destination.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}