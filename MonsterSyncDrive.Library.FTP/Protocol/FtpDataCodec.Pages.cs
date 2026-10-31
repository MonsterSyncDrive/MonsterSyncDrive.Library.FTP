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

using System.Runtime.CompilerServices;

namespace MonsterSyncDrive.Library.FTP.Protocol;

/// <summary>RFC 959 页结构的逻辑字节打包；页头不是固定四个八位字节，TYPE L n 时每个字段占 n 位。</summary>
public static partial class FtpDataCodec
{
    /// <summary>解码独立索引页、描述页及访问控制页；最后页不作为应用数据返回。</summary>
    public static async IAsyncEnumerable<FtpPage> ReadPagesAsync(Stream source, FtpTransferSettings settings,
        bool leaveOpen = true, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidatePageSettings(settings);
        await using FtpDataReadStream decoder = CreateReadStream(source, settings, leaveOpen);
        LogicalByteReader bits = new(decoder, settings.DataType == FtpDataType.Local ? settings.LocalByteSize : 8);
        while (true)
        {
            var headerLength = await bits.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (headerLength is < 4 or > 4096)
                throw new FtpProtocolException("FTP 页头长度无效或超过有界限制。");
            var index = await bits.ReadAsync(cancellationToken).ConfigureAwait(false);
            var dataLength = await bits.ReadAsync(cancellationToken).ConfigureAwait(false);
            var typeValue = await bits.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (typeValue > 3)
                throw new FtpProtocolException("FTP 页类型不受支持。");
            FtpPageType type = (FtpPageType)typeValue;
            if (type == FtpPageType.Last)
            {
                if (headerLength != 4 || dataLength != 0)
                    throw new FtpProtocolException("最后页必须有四字段页头和零数据长度。");
                bits.ValidatePadding();
                // 页的 Last 已提供结构 EOF；块/C/Z 模式仍需消费其自身的 EOF/校验尾部。
                // 普通 S/P 允许保留数据连接，不在 Last 后等待物理 TCP 关闭。
                if (settings.Mode != FtpTransmissionMode.Stream && await decoder.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
                    throw new FtpProtocolException("FTP 最后页之后仍包含额外数据。");
                yield break;
            }
            if ((type == FtpPageType.Simple && headerLength != 4) ||
                (type == FtpPageType.AccessControlled && headerLength != 5))
                throw new FtpProtocolException("FTP 页类型与页头长度不一致。");
            if (dataLength > (ulong)settings.MaxPageLogicalBytes)
                throw new FtpProtocolException("FTP 页数据超过逻辑字节数限制。");
            var extensions = new ulong[(int)headerLength - 4];
            for (var position = 0; position < extensions.Length; position++)
                extensions[position] = await bits.ReadAsync(cancellationToken).ConfigureAwait(false);
            var data = new ulong[(int)dataLength];
            for (var position = 0; position < data.Length; position++)
                data[position] = await bits.ReadAsync(cancellationToken).ConfigureAwait(false);
            yield return new FtpPage
            {
                Index = index,
                Type = type,
                Data = Array.AsReadOnly(data),
                HeaderExtensions = Array.AsReadOnly(extensions)
            };
        }
    }

    /// <summary>编码页头与逻辑数据并自动发送 Last 页；位值从高位到低位紧密打包，末尾不足八位时补零。</summary>
    public static async Task WritePagesAsync(Stream destination, IEnumerable<FtpPage> pages,
        FtpTransferSettings settings, bool leaveOpen = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ValidatePageSettings(settings);
        await using FtpDataWriteStream encoder = CreateWriteStream(destination, settings, leaveOpen);
        var width = settings.DataType == FtpDataType.Local ? settings.LocalByteSize : 8;
        LogicalByteWriter bits = new(encoder, width);
        var maximum = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        foreach (FtpPage page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(page);
            ArgumentNullException.ThrowIfNull(page.Data);
            ArgumentNullException.ThrowIfNull(page.HeaderExtensions);
            if (!Enum.IsDefined(page.Type) || page.Type == FtpPageType.Last)
                throw new ArgumentException("应用页必须是 Simple、Descriptor 或 AccessControlled，Last 由编码器生成。", nameof(pages));
            if (page.Data.Count > settings.MaxPageLogicalBytes || page.HeaderExtensions.Count > 4092)
                throw new ArgumentException("页数据或页头超过配置的长度限制。", nameof(pages));
            var data = page.Data.ToArray();
            var extensions = page.HeaderExtensions.ToArray();
            if ((page.Type == FtpPageType.Simple && extensions.Length != 0) ||
                (page.Type == FtpPageType.AccessControlled && extensions.Length != 1))
                throw new ArgumentException("页扩展字段数量与页类型不一致。", nameof(pages));
            var headerLength = checked((ulong)extensions.Length + 4);
            if (headerLength > maximum || (ulong)data.Length > maximum || page.Index > maximum ||
                data.Any(value => value > maximum) || extensions.Any(value => value > maximum))
                throw new ArgumentException("页字段不能放入 TYPE 指定的逻辑字节位宽。", nameof(pages));

            await bits.WriteAsync(headerLength, cancellationToken).ConfigureAwait(false);
            await bits.WriteAsync(page.Index, cancellationToken).ConfigureAwait(false);
            await bits.WriteAsync((ulong)data.Length, cancellationToken).ConfigureAwait(false);
            await bits.WriteAsync((ulong)page.Type, cancellationToken).ConfigureAwait(false);
            foreach (var extension in extensions)
                await bits.WriteAsync(extension, cancellationToken).ConfigureAwait(false);
            foreach (var value in data)
                await bits.WriteAsync(value, cancellationToken).ConfigureAwait(false);
        }
        await bits.WriteAsync(4, cancellationToken).ConfigureAwait(false);
        await bits.WriteAsync(0, cancellationToken).ConfigureAwait(false);
        await bits.WriteAsync(0, cancellationToken).ConfigureAwait(false);
        await bits.WriteAsync(0, cancellationToken).ConfigureAwait(false);
        await bits.CompleteAsync(cancellationToken).ConfigureAwait(false);
        await encoder.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>提前验证页结构与逻辑位宽，避免发送一半页头才发现参数不合法。</summary>
    private static void ValidatePageSettings(FtpTransferSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (settings.Structure != FtpFileStructure.Page)
            throw new ArgumentException("页 API 要求 STRU P。", nameof(settings));
    }

    /// <summary>从八位传输流读取 n 位逻辑字节，跨传输字节时不引入填充。</summary>
    private sealed class LogicalByteReader
    {
        /// <summary>已完成 MODE 解码的源流。</summary>
        private readonly Stream _stream;
        /// <summary>每个逻辑字节的位数。</summary>
        private readonly int _width;
        /// <summary>传输字节缓冲。</summary>
        private readonly byte[] _one = new byte[1];
        /// <summary>尚未消费的传输字节低位。</summary>
        private int _bits;
        /// <summary>缓冲中可用位数。</summary>
        private int _available;

        /// <summary>初始化有界逻辑字节读取器。</summary>
        internal LogicalByteReader(Stream stream, int width)
        {
            _stream = stream;
            _width = width;
        }

        /// <summary>读取恰好一个逻辑字节；页头或数据截断均视为协议错误。</summary>
        internal async Task<ulong> ReadAsync(CancellationToken cancellationToken)
        {
            ulong result = 0;
            for (var bit = 0; bit < _width; bit++)
            {
                if (_available == 0)
                {
                    if (await _stream.ReadAsync(_one, cancellationToken).ConfigureAwait(false) == 0)
                        throw new FtpProtocolException("FTP 页头或逻辑页数据被截断。");
                    _bits = _one[0];
                    _available = 8;
                }
                _available--;
                result = (result << 1) | (uint)((_bits >> _available) & 1);
            }
            return result;
        }

        /// <summary>页传输末尾只允许不足一个传输字节的零填充。</summary>
        internal void ValidatePadding()
        {
            if (_available > 0 && (_bits & ((1 << _available) - 1)) != 0)
                throw new FtpProtocolException("FTP 页结构末尾包含非零填充位。");
        }
    }

    /// <summary>将 n 位逻辑字节紧密打包到八位传输字节，不丢失 36/64 位页字段。</summary>
    private sealed class LogicalByteWriter
    {
        /// <summary>接受表示字节并执行 MODE 编码的流。</summary>
        private readonly Stream _stream;
        /// <summary>逻辑字节位宽。</summary>
        private readonly int _width;
        /// <summary>有界传输缓冲。</summary>
        private readonly byte[] _buffer = new byte[8192];
        /// <summary>缓冲中的完整传输字节数。</summary>
        private int _count;
        /// <summary>尚未形成完整传输字节的位。</summary>
        private int _pending;
        /// <summary>尚未形成完整传输字节的有效位数。</summary>
        private int _used;

        /// <summary>初始化逻辑位打包器。</summary>
        internal LogicalByteWriter(Stream stream, int width)
        {
            _stream = stream;
            _width = width;
        }

        /// <summary>按高位优先顺序写一个逻辑字节，整个文件只在最后一次补零。</summary>
        internal async Task WriteAsync(ulong value, CancellationToken cancellationToken)
        {
            for (var bit = _width - 1; bit >= 0; bit--)
            {
                _pending = (_pending << 1) | (int)((value >> bit) & 1);
                if (++_used == 8)
                {
                    _buffer[_count++] = (byte)_pending;
                    _used = 0;
                    _pending = 0;
                    if (_count == _buffer.Length)
                    {
                        await _stream.WriteAsync(_buffer, cancellationToken).ConfigureAwait(false);
                        _count = 0;
                    }
                }
            }
        }

        /// <summary>补齐最后一个传输字节，并刷新剩余的有界数据缓冲。</summary>
        internal async Task CompleteAsync(CancellationToken cancellationToken)
        {
            if (_used != 0)
                _buffer[_count++] = (byte)(_pending << (8 - _used));
            if (_count != 0)
                await _stream.WriteAsync(_buffer.AsMemory(0, _count), cancellationToken).ConfigureAwait(false);
            _count = 0;
            _used = 0;
            _pending = 0;
        }
    }
}