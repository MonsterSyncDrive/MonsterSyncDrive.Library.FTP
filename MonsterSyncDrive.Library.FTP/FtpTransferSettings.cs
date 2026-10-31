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

using System.Globalization;
using System.IO.Compression;

namespace MonsterSyncDrive.Library.FTP;

/// <summary>RFC 959 TYPE 数据表示；与控制命令、目录名称使用的编码相互独立。</summary>
public enum FtpDataType
{
    /// <summary>七位 ASCII 文本，使用 NVT CRLF 换行。</summary>
    Ascii,
    /// <summary>IBM EBCDIC CP037 文本，使用 EBCDIC NL 换行。</summary>
    Ebcdic,
    /// <summary>连续二进制位流，按八位传输字节发送。</summary>
    Image,
    /// <summary>调用者定义逻辑字节大小的连续紧密打包位流。</summary>
    Local
}

/// <summary>ASCII/EBCDIC 的垂直格式说明；库保留打印控制符，不执行打印排版。</summary>
public enum FtpTextFormat
{
    /// <summary>N：非打印格式。</summary>
    NonPrint,
    /// <summary>T：保留 CR/LF/VT/FF 等 Telnet 格式控制。</summary>
    Telnet,
    /// <summary>C：保留每行开头的 ASA/FORTRAN 纸张移动控制符。</summary>
    Asa
}

/// <summary>FTP 数据传输模式；Block/Compressed 与 Deflate 是不同的网络编码。</summary>
public enum FtpTransmissionMode
{
    /// <summary>S：普通流；文件结构由 TCP EOF 结束，记录结构有显式转义。</summary>
    Stream,
    /// <summary>B：三字节块头，支持 EOR、EOF、疑似错误和重启标记。</summary>
    Block,
    /// <summary>C：RFC 959 的字节串、重复字节及填充压缩。</summary>
    Compressed,
    /// <summary>Z：MODE Z 草案规定的 zlib/DEFLATE 流。</summary>
    Deflate
}

/// <summary>RFC 959 文件结构。</summary>
public enum FtpFileStructure
{
    /// <summary>F：连续文件。</summary>
    File,
    /// <summary>R：明确记录边界；普通 Stream API 以 LF 表示边界，记录 API 保留任意二进制记录。</summary>
    Record,
    /// <summary>P：带逻辑索引、页类型及访问控制的独立页，使用专用页 API。</summary>
    Page
}

/// <summary>块/压缩模式中接收的重启检查点；标记由发送端定义，不能擅自解释为字节偏移。</summary>
public sealed record FtpRestartMarker
{
    /// <summary>发送端提供的、无空格的可打印 ASCII 标记。</summary>
    public required string Marker { get; init; }

    /// <summary>本机此检查点之前已解码的应用字节数，可用于保存本地对应位置。</summary>
    public long DecodedOffset { get; init; }
}

/// <summary>
/// 不可变的数据表示及分帧配置。TYPE A/E 的应用端文本按 Latin1 字节表示，换行规范化为 LF；
/// TYPE A 拒绝高位字符。TYPE L 普通流直接暴露紧密打包位流，避免猜测本机逻辑字节填充。
/// </summary>
public sealed record FtpTransferSettings
{
    /// <summary>文件数据的表示类型；默认二进制。</summary>
    public FtpDataType DataType { get; init; } = FtpDataType.Image;

    /// <summary>ASCII/EBCDIC 的格式参数；Telnet/ASA 控制符由文件消费者解释，库原样保留。</summary>
    public FtpTextFormat TextFormat { get; init; } = FtpTextFormat.NonPrint;

    /// <summary>
    /// 输入/输出已经采用所选 TYPE 的网络表示时，跳过应用文本转换而保留 MODE/STRU 分帧。
    /// 适用于含 UTF-8 名称的 LIST/NLST：其隐含 ASCII 表示仍决定 MODE C 的空格填充，名称高位字节必须保留。
    /// 普通文件默认 false，以实际执行 ASCII/EBCDIC 转换。
    /// </summary>
    public bool PreserveRepresentation { get; init; }

    /// <summary>传输模式；默认流模式。</summary>
    public FtpTransmissionMode Mode { get; init; }

    /// <summary>文件结构；默认连续文件。</summary>
    public FtpFileStructure Structure { get; init; }

    /// <summary>TYPE L 的逻辑字节位数。连续流允许 1..65535；页 API 使用 ulong，限制为 3..64（页头长度 4 至少需要三位）。</summary>
    public int LocalByteSize { get; init; } = 8;

    /// <summary>MODE Z 的 zlib 压缩级别，不改变 MODE C 的 RFC 编码。</summary>
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Optimal;

    /// <summary>一次数据流最多允许解码的应用字节数，用于限制不可信压缩流。</summary>
    public long MaxDecodedBytes { get; init; } = long.MaxValue;

    /// <summary>单个显式记录可占用的最大应用字节数。</summary>
    public int MaxRecordBytes { get; init; } = 1048576;

    /// <summary>一个页允许的最大逻辑数据字节数；每个逻辑字节以 ulong 表示。</summary>
    public int MaxPageLogicalBytes { get; init; } = 1048576;

    /// <summary>是否拒绝服务器标记为疑似出错的数据；设置 false 可读取并观察 HasSuspectedData。</summary>
    public bool RejectSuspectedData { get; init; } = true;

    /// <summary>收到数据通道重启检查点时的同步通知；调用者可保存标记和对应的本地偏移。</summary>
    public Action<FtpRestartMarker>? RestartMarkerReceived { get; init; }

    /// <summary>校验枚举、逻辑字节大小和资源限额；不发送任何命令。</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(DataType)) throw new ArgumentOutOfRangeException(nameof(DataType));
        if (!Enum.IsDefined(TextFormat)) throw new ArgumentOutOfRangeException(nameof(TextFormat));
        if (!Enum.IsDefined(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        if (!Enum.IsDefined(Structure)) throw new ArgumentOutOfRangeException(nameof(Structure));
        if (!Enum.IsDefined(CompressionLevel)) throw new ArgumentOutOfRangeException(nameof(CompressionLevel));
        if (LocalByteSize is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(LocalByteSize));
        if (MaxDecodedBytes < 0) throw new ArgumentOutOfRangeException(nameof(MaxDecodedBytes));
        if (MaxRecordBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxRecordBytes));
        if (MaxPageLogicalBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxPageLogicalBytes));
        if (Structure == FtpFileStructure.Page && DataType is not (FtpDataType.Image or FtpDataType.Local))
            throw new ArgumentException("页结构必须使用 Image 或 Local 的逻辑二进制表示。");
        if (Structure == FtpFileStructure.Page && DataType == FtpDataType.Local && LocalByteSize is < 3 or > 64)
            throw new ArgumentOutOfRangeException(nameof(LocalByteSize), "页 API 的逻辑字节使用 3..64 位 ulong，且须能表示最短页头长度 4。");
    }

    /// <summary>验证并取得供一次传输持有的配置快照。</summary>
    public FtpTransferSettings Snapshot() { Validate(); return this with { }; }

    /// <summary>生成 TYPE 参数，例如 A N、E T、I 或 L 36。</summary>
    public string GetTypeArgument()
    {
        Validate();
        var format = TextFormat switch { FtpTextFormat.NonPrint => "N", FtpTextFormat.Telnet => "T", _ => "C" };
        return DataType switch
        {
            FtpDataType.Ascii => "A " + format,
            FtpDataType.Ebcdic => "E " + format,
            FtpDataType.Image => "I",
            _ => "L " + LocalByteSize.ToString(CultureInfo.InvariantCulture)
        };
    }

    /// <summary>生成 MODE 参数 S、B、C 或 Z。</summary>
    public string GetModeArgument()
    {
        Validate();
        return Mode switch { FtpTransmissionMode.Stream => "S", FtpTransmissionMode.Block => "B", FtpTransmissionMode.Compressed => "C", _ => "Z" };
    }

    /// <summary>生成 STRU 参数 F、R 或 P。</summary>
    public string GetStructureArgument()
    {
        Validate();
        return Structure switch { FtpFileStructure.File => "F", FtpFileStructure.Record => "R", _ => "P" };
    }
}

/// <summary>RFC 959 页类型。</summary>
public enum FtpPageType
{
    /// <summary>0：最后页；由页编码器自动发送。</summary>
    Last,
    /// <summary>1：普通页，固定四个逻辑字节的页头。</summary>
    Simple,
    /// <summary>2：文件描述页，可带附加页头字段。</summary>
    Descriptor,
    /// <summary>3：访问控制页，固定五个逻辑字节的页头。</summary>
    AccessControlled
}

/// <summary>一页的独立索引、逻辑数据及可选头字段；每个值占 TYPE 指定的一个逻辑字节。</summary>
public sealed record FtpPage
{
    /// <summary>页的逻辑索引，而非传输序号。</summary>
    public ulong Index { get; init; }

    /// <summary>普通、描述或访问控制页；Last 终止页由编码器生成。</summary>
    public FtpPageType Type { get; init; } = FtpPageType.Simple;

    /// <summary>完整的逻辑数据值；位宽由 TYPE I 的 8 位或 TYPE L n 决定。</summary>
    public IReadOnlyList<ulong> Data { get; init; } = Array.Empty<ulong>();

    /// <summary>第四个头字段之后的扩展字段。访问控制页恰好一个字段；普通页没有扩展。</summary>
    public IReadOnlyList<ulong> HeaderExtensions { get; init; } = Array.Empty<ulong>();
}