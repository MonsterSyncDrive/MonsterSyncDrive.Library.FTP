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

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MonsterSyncDrive.Library.FTP.Protocol;

/// <summary>有界、流式的 FTP 数据解码器，支持 S/B/C/Z、记录边界及 ASCII/EBCDIC 表示。</summary>
public sealed class FtpDataReadStream : Stream
{
    /// <summary>调用者拥有的原始网络数据流。</summary>
    private readonly Stream _source;
    /// <summary>原始流或 MODE Z 的 zlib 解码流。</summary>
    private readonly Stream _wire;
    /// <summary>每个解码器独立的配置快照。</summary>
    private readonly FtpTransferSettings _settings;
    /// <summary>释放时是否保留原始流。</summary>
    private readonly bool _leaveOpen;
    /// <summary>一次单字节分帧读取的缓冲。</summary>
    private readonly byte[] _one = new byte[1];
    /// <summary>当前已解码但尚未交付的应用数据。</summary>
    private byte[] _decoded = [];
    /// <summary>应用数据读取位置。</summary>
    private int _offset;
    /// <summary>是否已经消费协议的 EOF 标记。</summary>
    private bool _wireEnded;
    /// <summary>跨网络块保留的 ASCII CR。</summary>
    private bool _pendingCarriageReturn;
    /// <summary>全部已产生的应用字节，包含普通流表示的记录 LF。</summary>
    private long _decodedCount;
    /// <summary>MODE C 下一字节串的描述标志。</summary>
    private byte _compressedDescriptor;
    /// <summary>混合字节流/显式记录读取的防护，0 为未选，1 为流，2 为记录。</summary>
    private int _api;
    /// <summary>自上次 EOR 后是否已经出现记录数据，用于拒绝缺少末条 EOR 的 EOF。</summary>
    private bool _recordOpen;
    /// <summary>是否已经释放本包装器。</summary>
    private bool _disposed;
    /// <summary>EBCDIC CP037 到应用 Latin1 的映射表。</summary>
    private static readonly byte[] EbcdicDecode = CreateEbcdicDecode();

    /// <summary>建立由工厂校验过的解码器；包装器不实施控制连接协议。</summary>
    internal FtpDataReadStream(Stream source, FtpTransferSettings settings, bool leaveOpen)
    {
        _source = source;
        _settings = settings;
        _leaveOpen = leaveOpen;
        _wire = settings.Mode == FtpTransmissionMode.Deflate
            ? new ZLibStream(source, CompressionMode.Decompress, leaveOpen: true) : source;
    }

    /// <summary>最后一次接收到的发送端重启标记。</summary>
    public FtpRestartMarker? LastRestartMarker
    {
        get; private set;
    }
    /// <summary>服务器是否曾把数据块标记为疑似错误；默认策略会直接拒绝此类数据。</summary>
    public bool HasSuspectedData
    {
        get; private set;
    }
    /// <summary>尚未释放时可读。</summary>
    public override bool CanRead => !_disposed;
    /// <summary>数据编码器不允许定位。</summary>
    public override bool CanSeek => false;
    /// <summary>读取解码器不允许写入。</summary>
    public override bool CanWrite => false;
    /// <summary>网络表示没有可查询的应用长度。</summary>
    public override long Length => throw new NotSupportedException();
    /// <summary>网络解码流不可定位。</summary>
    public override long Position
    {
        get => throw new NotSupportedException(); set => throw new NotSupportedException();
    }

    /// <summary>异步读取解码后的应用字节；记录结构的普通流用 LF 表示每一个 EOR。</summary>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_api == 2)
            throw new InvalidOperationException("不能在同一数据流混用字节读取和记录读取。");
        _api = 1;
        if (buffer.Length == 0)
            return 0;
        while (_offset == _decoded.Length)
        {
            Frame? frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
                return 0;
            _decoded = DecodeRepresentation(frame.Data, (frame.Descriptor & 0xc0) != 0);
            if (_settings.Structure == FtpFileStructure.Record && (frame.Descriptor & 0x80) != 0)
                _decoded = [.. _decoded, 10];
            AddDecoded(_decoded.Length);
            _offset = 0;
        }
        int count = Math.Min(buffer.Length, _decoded.Length - _offset);
        _decoded.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    /// <summary>读取一个明确的结构记录，保留记录内容中的 LF。文件结束后返回空引用。</summary>
    public async Task<byte[]?> ReadRecordAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_settings.Structure != FtpFileStructure.Record)
            throw new InvalidOperationException("此数据流没有 STRU R。");
        if (_api == 1)
            throw new InvalidOperationException("不能在同一数据流混用字节读取和记录读取。");
        _api = 2;
        using var record = new MemoryStream();
        while (true)
        {
            Frame? frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                return record.Length != 0 ? throw new FtpProtocolException("记录结构的最后一条记录缺少 EOR。") : null;
            }
            byte[] data = DecodeRepresentation(frame.Data, (frame.Descriptor & 0xc0) != 0);
            if (record.Length + data.Length > _settings.MaxRecordBytes)
                throw new FtpProtocolException("FTP 记录超过配置的长度限制。");
            AddDecoded(data.Length);
            record.Write(data);
            if ((frame.Descriptor & 0x80) != 0)
                return record.ToArray();
            if ((frame.Descriptor & 0x40) != 0)
            {
                return record.Length != 0 ? throw new FtpProtocolException("记录结构的 EOF 缺少最后一条 EOR。") : null;
            }
        }
    }

    /// <summary>读取一个含数据、EOR 或 EOF 的传输单元；标记块只更新检查点，不混入文件。</summary>
    private async Task<Frame?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        if (_wireEnded)
            return null;
        for (int emptyFrames = 0; emptyFrames < 1024; emptyFrames++)
        {
            Frame? frame;
            if (_settings.Mode is FtpTransmissionMode.Stream or FtpTransmissionMode.Deflate)
            {
                if (_settings.Structure == FtpFileStructure.Record)
                    frame = await ReadStreamRecordFrameAsync(cancellationToken).ConfigureAwait(false);
                else
                {
                    byte[] block = new byte[8192];
                    int count = await _wire.ReadAsync(block, cancellationToken).ConfigureAwait(false);
                    frame = count == 0 ? new Frame([], 0x40) : new Frame(block[..count], 0);
                }
            }
            else if (_settings.Mode == FtpTransmissionMode.Block)
            {
                byte[] header = new byte[3];
                await ReadFrameBytesAsync(header, cancellationToken).ConfigureAwait(false);
                byte[] data = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1))];
                await ReadFrameBytesAsync(data, cancellationToken).ConfigureAwait(false);
                frame = new Frame(data, header[0]);
            }
            else
                frame = await ReadCompressedFrameAsync(cancellationToken).ConfigureAwait(false);

            if ((frame.Descriptor & 0x0f) != 0)
                throw new FtpProtocolException("FTP 数据描述符包含保留位。");
            if ((frame.Descriptor & 0x20) != 0)
            {
                HasSuspectedData = true;
                if (_settings.RejectSuspectedData)
                    throw new FtpProtocolException("服务器把此数据块标记为疑似出错。");
            }
            if ((frame.Descriptor & 0x40) != 0)
                _wireEnded = true;
            if ((frame.Descriptor & 0x10) != 0)
            {
                if (frame.Data.Length == 0 || frame.Data.Any(static value => value is < 33 or > 126))
                    throw new FtpProtocolException("重启标记必须是无空格的可打印 ASCII 字符。");
                LastRestartMarker = new FtpRestartMarker { Marker = Encoding.ASCII.GetString(frame.Data), DecodedOffset = _decodedCount };
                _settings.RestartMarkerReceived?.Invoke(LastRestartMarker);
                if ((frame.Descriptor & 0xc0) != 0)
                    return new Frame([], (byte)(frame.Descriptor & 0xc0));
                continue;
            }
            if (_settings.Structure == FtpFileStructure.Record)
            {
                if (frame.Data.Length != 0)
                    _recordOpen = true;
                if ((frame.Descriptor & 0x80) != 0)
                    _recordOpen = false;
                if ((frame.Descriptor & 0x40) != 0 && _recordOpen)
                    throw new FtpProtocolException("记录数据在最后一条 EOR 前结束。");
            }
            if (frame.Data.Length == 0 && frame.Descriptor == 0)
                continue;
            return frame;
        }
        throw new FtpProtocolException("FTP 数据流包含过多不产生数据的空单元。");
    }

    /// <summary>读取 STRU R 的 FF/01 EOR、FF/02 EOF、FF/03 联合标记和 FF/FF 数据转义。</summary>
    private async Task<Frame> ReadStreamRecordFrameAsync(CancellationToken cancellationToken)
    {
        using var data = new MemoryStream();
        while (data.Length < 8192)
        {
            int value = await ReadOneAsync(cancellationToken).ConfigureAwait(false);
            if (value < 0)
                throw new FtpProtocolException("记录数据流在显式 EOF 前关闭。");
            if (value != 255)
            {
                data.WriteByte((byte)value);
                continue;
            }
            int escape = await ReadOneAsync(cancellationToken).ConfigureAwait(false);
            if (escape == 255)
            {
                data.WriteByte(255);
                continue;
            }
            return escape is < 1 or > 3
                ? throw new FtpProtocolException("记录数据流的 FF 转义无效。")
                : new Frame(data.ToArray(), (byte)(((escape & 1) != 0 ? 0x80 : 0) | ((escape & 2) != 0 ? 0x40 : 0)));
        }
        return new Frame(data.ToArray(), 0);
    }

    /// <summary>
    /// 解析 MODE C 的字节串、六位重复数和类型相关填充字节。
    /// 描述符作用于随后的压缩单元；EOF 后的最后一个单元读完即可结束，不依赖关闭 TCP。
    /// 零数据 EOF 使用独立终止描述符并以物理 EOF 结束，避免把后续传输内容猜测成当前文件。
    /// </summary>
    private async Task<Frame> ReadCompressedFrameAsync(CancellationToken cancellationToken)
    {
        for (int controls = 0; controls < 1024; controls++)
        {
            int prefix = await ReadOneAsync(cancellationToken).ConfigureAwait(false);
            if (prefix < 0)
            {
                if ((_compressedDescriptor & 0x40) == 0)
                    throw new FtpProtocolException("MODE C 在 EOF 描述符前关闭。");
                byte terminal = _compressedDescriptor;
                _compressedDescriptor = 0;
                return new Frame([], terminal);
            }
            if (prefix == 0)
            {
                int descriptor = await ReadOneAsync(cancellationToken).ConfigureAwait(false);
                if (descriptor < 0)
                    throw new FtpProtocolException("MODE C 描述符被截断。");
                if (_compressedDescriptor != 0)
                {
                    // 连续的结构控制表示空记录。将旧控制交付，再保留下一个控制供下一轮消费。
                    byte previous = _compressedDescriptor;
                    _compressedDescriptor = (byte)descriptor;
                    return new Frame([], previous);
                }
                _compressedDescriptor = (byte)descriptor;
                continue;
            }
            byte[] data;
            if ((prefix & 0x80) == 0)
            {
                data = new byte[prefix];
                await ReadFrameBytesAsync(data, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                int count = prefix & 0x3f;
                if (count == 0)
                    throw new FtpProtocolException("MODE C 的重复/填充计数必须为正。");
                int repeated = (prefix & 0x40) != 0 ? FillerByte() : await ReadOneAsync(cancellationToken).ConfigureAwait(false);
                if (repeated < 0)
                    throw new FtpProtocolException("MODE C 重复字节被截断。");
                data = new byte[count];
                Array.Fill(data, (byte)repeated);
            }
            byte flags = _compressedDescriptor;
            _compressedDescriptor = 0;
            return new Frame(data, flags);
        }
        throw new FtpProtocolException("MODE C 包含过多连续控制单元。");
    }

    /// <summary>将数据表示转换为应用端 Latin1/ASCII 字节；换行统一为 LF，格式控制符保持原义。</summary>
    private byte[] DecodeRepresentation(byte[] data, bool boundary)
    {
        if (_settings.PreserveRepresentation || _settings.DataType is FtpDataType.Image or FtpDataType.Local)
            return data;
        var result = new List<byte>(data.Length + 1);
        foreach (byte raw in data)
        {
            if (_settings.DataType == FtpDataType.Ebcdic)
            {
                result.Add(raw == 0x15 ? (byte)10 : EbcdicDecode[raw]);
                continue;
            }
            if (raw > 127)
                throw new FtpProtocolException("TYPE A 包含非七位 ASCII 数据。");
            if (_pendingCarriageReturn)
            {
                if (raw == 10)
                    result.Add(10);
                else if (raw == 0)
                    result.Add(13);
                else
                    throw new FtpProtocolException("NVT ASCII 的 CR 后必须是 LF 或 NUL。");
                _pendingCarriageReturn = false;
            }
            else if (raw == 13)
                _pendingCarriageReturn = true;
            else
                result.Add(raw);
        }
        return boundary && _pendingCarriageReturn ? throw new FtpProtocolException("ASCII 数据在 CR 转义完成前结束。") : result.ToArray();
    }

    /// <summary>增加已解码字节预算，先检查剩余空间以避免整数溢出。</summary>
    private void AddDecoded(int count)
    {
        if (count > _settings.MaxDecodedBytes - _decodedCount)
            throw new FtpProtocolException("解码数据超过配置的字节限制。");
        _decodedCount += count;
    }

    /// <summary>按表示类型确定 MODE C 的填充值。</summary>
    private int FillerByte()
    {
        return _settings.DataType switch
        {
            FtpDataType.Ascii => 32,
            FtpDataType.Ebcdic => 64,
            _ => 0
        };
    }

    /// <summary>读取一个传输字节；物理 EOF 返回 -1。</summary>
    private async Task<int> ReadOneAsync(CancellationToken cancellationToken)
    {
        return await _wire.ReadAsync(_one, cancellationToken).ConfigureAwait(false) == 0 ? -1 : _one[0];
    }

    /// <summary>完整读取有界头部或数据体；不允许把截断帧当成正常文件结束。</summary>
    private async Task ReadFrameBytesAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int position = 0;
        while (position < buffer.Length)
        {
            int read = await _wire.ReadAsync(buffer[position..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new FtpProtocolException("FTP 数据单元被截断。");
            position += read;
        }
    }

    /// <summary>通过 BCL provider 创建映射，不向进程全局注册或引入第三方包。</summary>
    private static byte[] CreateEbcdicDecode()
    {
        Encoding encoding = CodePagesEncodingProvider.Instance.GetEncoding(37) ?? throw new NotSupportedException("BCL 不提供 EBCDIC CP037。");
        byte[] values = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
        char[] characters = encoding.GetChars(values);
        return characters.Select(static character => character <= 255 ? (byte)character :
            throw new NotSupportedException("CP037 包含不能表示为 Latin1 的字符。")).ToArray();
    }

    /// <summary>兼容数组形式的异步读取。</summary>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>兼容同步 Stream 调用。</summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>读取包装器没有可刷新的写缓冲。</summary>
    public override void Flush()
    {
    }
    /// <summary>拒绝在解码器写入。</summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    /// <summary>拒绝定位到不完整的压缩/分帧位置。</summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    /// <summary>读取包装器不能改变长度。</summary>
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <summary>同步释放 zlib 本机状态，并根据 leaveOpen 决定是否释放原始流。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (!ReferenceEquals(_wire, _source))
                _wire.Dispose();
            if (!_leaveOpen)
                _source.Dispose();
        }
        base.Dispose(disposing);
    }
    /// <summary>异步释放 zlib 状态和可选的原始流。</summary>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!ReferenceEquals(_wire, _source))
            await _wire.DisposeAsync().ConfigureAwait(false);
        if (!_leaveOpen)
            await _source.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>一个完整传输单元；描述符的 EOR/EOF 作用于 Data 末尾。</summary>
    /// <param name="Data">不含帧头的网络表示字节。</param>
    /// <param name="Descriptor">RFC 959 的描述位。</param>
    private sealed record Frame(byte[] Data, byte Descriptor);
}