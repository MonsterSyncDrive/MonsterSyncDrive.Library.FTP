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
using System.Globalization;

namespace MonsterSyncDrive.Library.FTP.Protocol;

/// <summary>FTP 控制通道的分帧器。严格读取完整多行回复，并实现 RFC 959 要求的 Telnet IAC 转义。</summary>
internal sealed class FtpControlChannel
{
    /// <summary>底层明文或 TLS 流；生命周期由会话持有。</summary>
    private readonly Stream _stream;

    /// <summary>当前会话参数及资源限制。</summary>
    private readonly FtpOptions _options;

    /// <summary>缓冲读取以避免每个字符产生一次套接字调用。</summary>
    private readonly byte[] _readBuffer = new byte[4096];

    /// <summary>读取缓冲区的下一个有效位置。</summary>
    private int _readOffset;

    /// <summary>读取缓冲区的有效字节数。</summary>
    private int _readCount;

    /// <summary>一次回复实际消费的字节数，包括 Telnet 协商数据。</summary>
    private int _replyBytes;

    /// <summary>建立控制通道分帧器；不取得底层流的释放责任。</summary>
    internal FtpControlChannel(Stream stream, FtpOptions options)
    {
        this._stream = stream;
        this._options = options;
    }

    /// <summary>写入一个命令，按 Telnet 规则把数据中的 0xff 字节加倍。</summary>
    internal async Task SendAsync(string command, string? argument, CancellationToken cancellationToken)
    {
        string text = argument is null ? command : command + " " + argument;
        byte[] bytes = this._options.Encoding.GetBytes(text);
        if (bytes.Length > this._options.MaxLineLength - 2)
            throw new ArgumentException("FTP 命令超过配置的最大行长度。", nameof(argument));
        var output = new ArrayBufferWriter<byte>(bytes.Length + 2);
        foreach (byte value in bytes)
        {
            output.GetSpan(1)[0] = value;
            output.Advance(1);
            if (value == 255)
            {
                output.GetSpan(1)[0] = value;
                output.Advance(1);
            }
        }
        output.GetSpan(2)[0] = 13;
        output.GetSpan(2)[1] = 10;
        output.Advance(2);
        await this._stream.WriteAsync(output.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await this._stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取一个完整回复。多行回复只能由相同状态码加空格结束，不能被其他数字行提前截断。</summary>
    internal async Task<FtpReply> ReadAsync(CancellationToken cancellationToken)
    {
        this._replyBytes = 0;
        string first = await this.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (first.Length < 3 || !int.TryParse(first.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int code) ||
            code is < 100 or > 699 || (first.Length > 3 && first[3] is not (' ' or '-')))
            throw new FtpProtocolException("服务器返回了无效的 FTP 状态行。");

        var lines = new List<string> { first };
        if (first.Length > 3 && first[3] == '-')
        {
            string prefix = first[..3] + " ";
            while (true)
            {
                if (lines.Count >= this._options.MaxReplyLines)
                    throw new FtpProtocolException("FTP 多行回复超过配置的行数限制。");
                string line = await this.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                lines.Add(line);
                if (line.StartsWith(prefix, StringComparison.Ordinal) || line == first[..3]) break;
            }
        }
        return new FtpReply(code, lines);
    }

    /// <summary>读取一行有界 CRLF 文本；拒绝半截行和不可解码的数据。</summary>
    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new ArrayBufferWriter<byte>();
        while (true)
        {
            byte value = await this.ReadTelnetByteAsync(cancellationToken).ConfigureAwait(false);
            if (value == 10)
            {
                return line.WrittenCount == 0 || line.WrittenSpan[^1] != 13
                    ? throw new FtpProtocolException("FTP 控制回复必须以 CRLF 结束。")
                    : this._options.Encoding.GetString(line.WrittenSpan[..^1]);
            }
            if (line.WrittenCount >= this._options.MaxLineLength)
                throw new FtpProtocolException("FTP 回复行超过配置的长度限制。");
            line.GetSpan(1)[0] = value;
            line.Advance(1);
        }
    }

    /// <summary>读取 Telnet 数据字节，拒绝所有可选协商，并跳过有界的子协商。</summary>
    private async Task<byte> ReadTelnetByteAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            byte value = await this.ReadRawByteAsync(cancellationToken).ConfigureAwait(false);
            if (value != 255) return value;
            byte command = await this.ReadRawByteAsync(cancellationToken).ConfigureAwait(false);
            if (command == 255) return 255;
            if (command is 251 or 252 or 253 or 254)
            {
                byte option = await this.ReadRawByteAsync(cancellationToken).ConfigureAwait(false);
                if (command is 251 or 253)
                    await this._stream.WriteAsync(new byte[] { 255, command == 251 ? (byte)254 : (byte)252, option }, cancellationToken)
                        .ConfigureAwait(false);
            }
            else if (command == 250)
            {
                // SB ... IAC SE：子协商也计入回复总字节预算，恶意对端不能无限发送协商流。
                while (true)
                {
                    if (await this.ReadRawByteAsync(cancellationToken).ConfigureAwait(false) is 255 and 240) break;
                }
            }
        }
    }

    /// <summary>返回一个缓冲字节；EOF 和回复总量超限视为连接不可再同步。</summary>
    private async Task<byte> ReadRawByteAsync(CancellationToken cancellationToken)
    {
        if (this._replyBytes >= this._options.MaxReplyBytes)
            throw new FtpProtocolException("FTP 回复超过配置的总字节限制。");
        this._replyBytes++;
        if (this._readOffset == this._readCount)
        {
            this._readCount = await this._stream.ReadAsync(this._readBuffer, cancellationToken).ConfigureAwait(false);
            this._readOffset = 0;
            if (this._readCount == 0) throw new EndOfStreamException("FTP 控制连接在回复完成前关闭。");
        }
        return this._readBuffer[this._readOffset++];
    }
}