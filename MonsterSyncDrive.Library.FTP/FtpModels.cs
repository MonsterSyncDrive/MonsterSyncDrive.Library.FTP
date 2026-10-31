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

using System.Collections.ObjectModel;

namespace MonsterSyncDrive.Library.FTP;

/// <summary>FTP 控制连接的安全模式。</summary>
public enum FtpEncryptionMode
{
    /// <summary>普通 FTP，凭据和数据以明文发送。</summary>
    None,
    /// <summary>先接收 FTP 欢迎语，再使用 AUTH TLS 升级连接；升级失败不会降级明文登录。</summary>
    ExplicitTls,
    /// <summary>TCP 建立后立即进行 TLS 握手，通常使用端口 990。</summary>
    ImplicitTls
}

/// <summary>FTP 数据连接建立策略。</summary>
public enum FtpDataConnectionMode
{
    /// <summary>优先 EPSV，仅在 IPv4 的服务器明确拒绝扩展命令时使用 PASV。</summary>
    AutoPassive,
    /// <summary>只使用扩展被动模式，兼容 IPv4 和 IPv6。</summary>
    ExtendedPassive,
    /// <summary>只使用传统 IPv4 被动模式。</summary>
    Passive,
    /// <summary>优先 EPRT，仅在 IPv4 的服务器明确拒绝扩展命令时使用 PORT。</summary>
    AutoActive,
    /// <summary>只使用扩展主动模式，兼容 IPv4 和 IPv6。</summary>
    ExtendedActive,
    /// <summary>只使用传统 IPv4 主动模式。</summary>
    Active,
    /// <summary>RFC 1639 历史长被动模式 LPSV，可携带 IPv4 或 IPv6 端点。</summary>
    LongPassive,
    /// <summary>RFC 1639 历史长主动模式 LPRT，可携带 IPv4 或 IPv6 端点。</summary>
    LongActive
}

/// <summary>远程条目的类型；链接不会被递归目录操作跟随。</summary>
public enum FtpItemType
{
    /// <summary>服务器未提供可识别的类型。</summary>
    Unknown,
    /// <summary>普通文件。</summary>
    File,
    /// <summary>目录。</summary>
    Directory,
    /// <summary>符号链接。</summary>
    SymbolicLink
}

/// <summary>目录列表命令选择策略。</summary>
public enum FtpListingMode
{
    /// <summary>优先 MLSD，仅遇到不支持命令的回复时回退 LIST。</summary>
    Auto,
    /// <summary>只使用 RFC 3659 的机器可读列表。</summary>
    Machine,
    /// <summary>使用传统 UNIX/DOS LIST 格式。</summary>
    Legacy
}

/// <summary>文件上传行为。</summary>
public enum FtpUploadMode
{
    /// <summary>使用 STOR 覆盖远程文件。</summary>
    Overwrite,
    /// <summary>使用 APPE 将整个源流追加到远程文件。</summary>
    Append,
    /// <summary>查询远程大小，定位本地文件，再用 REST 和 STOR 续传；只适用于本地文件 API。</summary>
    Resume
}

/// <summary>服务器摘要命令使用的算法；弱算法仅为兼容旧服务器提供。</summary>
public enum FtpHashAlgorithm
{
    /// <summary>SHA-256，默认算法。</summary>
    Sha256,
    /// <summary>SHA-512。</summary>
    Sha512,
    /// <summary>SHA-1，仅作非安全性文件一致性检查。</summary>
    Sha1,
    /// <summary>MD5，仅作非安全性文件一致性检查。</summary>
    Md5
}

/// <summary>不可变的远程文件或目录元数据。未知的大小、时间、权限保留为空。</summary>
public sealed record FtpItem
{
    /// <summary>服务器返回的条目名称；保留空格和 Unicode 字符，不进行 URI 解码。</summary>
    public required string Name
    {
        get; init;
    }

    /// <summary>用于后续 FTP 命令的完整远程路径。</summary>
    public required string FullPath
    {
        get; init;
    }

    /// <summary>文件、目录、链接或未知类型。</summary>
    public FtpItemType Type
    {
        get; init;
    }

    /// <summary>文件大小；服务器未提供或条目不是普通文件时可能为空。</summary>
    public long? Size
    {
        get; init;
    }

    /// <summary>RFC 3659 的 UTC 修改时间；传统 LIST 没有时区，见 ListingModifiedTime。</summary>
    public DateTimeOffset? ModifiedTime
    {
        get; init;
    }

    /// <summary>create fact 提供的 UTC 创建时间；未提供时为空，不从修改时间猜测。</summary>
    public DateTimeOffset? CreatedTime
    {
        get; init;
    }

    /// <summary>服务器提供的所有者名称或原始所有者标识，优先使用 UNIX.ownername。</summary>
    public string? Owner
    {
        get; init;
    }

    /// <summary>服务器提供的组名称或原始组标识，优先使用 UNIX.groupname。</summary>
    public string? Group
    {
        get; init;
    }

    /// <summary>明确的 UNIX.uid 数字用户标识；不将名称或 UNIX.owner 字符串猜成数字 UID。</summary>
    public long? UserId
    {
        get; init;
    }

    /// <summary>明确的 UNIX.gid 数字组标识。</summary>
    public long? GroupId
    {
        get; init;
    }

    /// <summary>UNIX.nlink 提供的非负硬链接数量。</summary>
    public long? LinkCount
    {
        get; init;
    }

    /// <summary>unique fact 的不透明服务器标识，保持原始大小写，仅在服务器规定的范围内有意义。</summary>
    public string? UniqueId
    {
        get; init;
    }

    /// <summary>perm fact 表示的当前用户操作权限；未公布时为空，明确空权限时为 None。</summary>
    public FtpPermissions? AllowedOperations
    {
        get; init;
    }

    /// <summary>charset fact 提供的文件内容字符集，不影响控制通道编码。</summary>
    public string? CharacterSet
    {
        get; init;
    }

    /// <summary>lang fact 提供的内容语言标记，不是当前控制回复语言。</summary>
    public string? Language
    {
        get; init;
    }

    /// <summary>media-type fact 提供的媒体类型，保持服务器原值。</summary>
    public string? MediaType
    {
        get; init;
    }

    /// <summary>传统 LIST 的服务器本地时间，Kind 为 Unspecified；不会伪装成准确 UTC 时间。</summary>
    public DateTime? ListingModifiedTime
    {
        get; init;
    }

    /// <summary>服务器返回的 UNIX 权限（例如 0755）或 LIST 权限字符串。</summary>
    public string? Permissions
    {
        get; init;
    }

    /// <summary>LIST 提供的符号链接目标；不会自动跟随。</summary>
    public string? LinkTarget
    {
        get; init;
    }

    /// <summary>原始目录行，用于故障诊断或处理服务器特有扩展。</summary>
    public string RawListing { get; init; } = string.Empty;

    /// <summary>大小写不敏感的 MLST/MLSD facts，包括未知扩展；字典只读。</summary>
    public IReadOnlyDictionary<string, string> Facts
    {
        get; init;
    } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>一次传输的进度快照；偏移和已传输量分别表示历史内容与本次数据。</summary>
public sealed record FtpTransferProgress
{
    /// <summary>传输使用的远程路径。</summary>
    public required string RemotePath
    {
        get; init;
    }

    /// <summary>本次传输已复制的字节数，不包含断点之前的数据。</summary>
    public long BytesTransferred
    {
        get; init;
    }

    /// <summary>文件断点偏移量。</summary>
    public long RestartOffset
    {
        get; init;
    }

    /// <summary>完整文件大小；服务器或源流无法确定时为空。</summary>
    public long? TotalBytes
    {
        get; init;
    }

    /// <summary>本次传输耗时。</summary>
    public TimeSpan Elapsed
    {
        get; init;
    }

    /// <summary>平均字节/秒；开始传输时为零。</summary>
    public double BytesPerSecond => Elapsed.TotalSeconds > 0 ? BytesTransferred / Elapsed.TotalSeconds : 0;

    /// <summary>包括断点内容的完成百分比；总大小未知时为空。</summary>
    public double? Percentage => TotalBytes is > 0 ? Math.Min(100, (RestartOffset + BytesTransferred) * 100d / TotalBytes.Value) :
        TotalBytes == 0 ? 100 : null;
}

/// <summary>服务器报告的文件摘要。</summary>
public sealed record FtpChecksum
{
    /// <summary>摘要算法。</summary>
    public required FtpHashAlgorithm Algorithm
    {
        get; init;
    }

    /// <summary>规范化为小写的十六进制摘要。</summary>
    public required string Value
    {
        get; init;
    }
}