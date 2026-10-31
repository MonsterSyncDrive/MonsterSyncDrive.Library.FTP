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

using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MonsterSyncDrive.Library.FTP;

/// <summary>FTP 会话配置。客户端构造时复制配置；连接后不会读取调用者对编码或证书集合的修改。</summary>
public sealed record FtpOptions
{
    /// <summary>服务器 DNS 名称或 IP 地址，不包含协议前缀、端口及路径。</summary>
    public required string Host { get; init; }

    /// <summary>可选的 FTP HOST 虚拟主机名称；在 AUTH/USER 前发送，与 TCP 连接地址和 TLS 证书名称分开。</summary>
    public string? VirtualHost { get; init; }

    /// <summary>可选 TLS 证书/SNI 主机名；为空时使用 VirtualHost 或 Host。</summary>
    public string? TlsTargetHost { get; init; }

    /// <summary>登录后应用的数据表示、传输模式和文件结构；默认二进制流式文件。</summary>
    public FtpTransferSettings TransferSettings { get; init; } = new();

    /// <summary>可选的非 TLS AUTH/ADAT 安全机制和命令保护策略。</summary>
    public FtpSecurityOptions? Security { get; init; }

    /// <summary>TLS 认证提供器。默认使用操作系统 SslStream；提供器可实现可验证的会话复用。</summary>
    public IFtpTlsProvider TlsProvider { get; init; } = FtpPlatformTlsProvider.Instance;

    /// <summary>数据 TLS 会话复用策略；严格策略要求提供器实际确认控制会话绑定。</summary>
    public FtpTlsSessionReusePolicy TlsSessionReuse { get; init; } = FtpTlsSessionReusePolicy.Allow;

    /// <summary>控制端口。零表示根据加密方式选择 21 或隐式 FTPS 的 990。</summary>
    public int Port { get; init; }

    /// <summary>登录用户名；默认使用匿名登录。</summary>
    public string UserName { get; init; } = "anonymous";

    /// <summary>登录密码。库不会把密码写入诊断消息或异常的命令名称。</summary>
    public string Password { get; init; } = "anonymous@";

    /// <summary>服务器返回 332 时提交的 ACCT 值；未配置时拒绝继续登录。</summary>
    public string? Account { get; init; }

    /// <summary>控制连接的加密方式；默认普通 FTP，生产环境建议显式 TLS。</summary>
    public FtpEncryptionMode Encryption { get; init; }

    /// <summary>FTPS 是否加密数据连接；默认使用 PROT P。</summary>
    public bool EncryptDataChannel { get; init; } = true;

    /// <summary>允许的 TLS 版本。默认让操作系统选择安全版本，不启用过时的 SSL。</summary>
    public SslProtocols EnabledSslProtocols { get; init; } = SslProtocols.None;

    /// <summary>服务器证书吊销检查方式；默认在线检查。</summary>
    public X509RevocationMode CertificateRevocationCheckMode { get; init; } = X509RevocationMode.Online;

    /// <summary>可选的证书验证回调，适用于私有 CA 或证书固定。为空时执行平台默认验证。</summary>
    public RemoteCertificateValidationCallback? CertificateValidation { get; init; }

    /// <summary>可选的 TLS 客户端证书；证书必须在会话结束前保持有效。</summary>
    public X509CertificateCollection? ClientCertificates { get; init; }

    /// <summary>数据连接方式；默认先 EPSV，在 IPv4 且明确不支持时回退 PASV。</summary>
    public FtpDataConnectionMode DataConnectionMode { get; init; } = FtpDataConnectionMode.AutoPassive;

    /// <summary>是否信任 PASV 回包中的 IP。默认忽略该地址并连接控制通道对端，避免 NAT 故障和地址跳转。</summary>
    public bool TrustPassiveAddress { get; init; }

    /// <summary>主动模式绑定地址。为空时使用控制连接本地地址；不可与控制连接地址族不同。</summary>
    public IPAddress? ActiveBindAddress { get; init; }

    /// <summary>主动模式公布给服务器的地址，用于已配置端口映射的 NAT；为空时使用绑定地址。</summary>
    public IPAddress? ActiveAdvertisedAddress { get; init; }

    /// <summary>主动模式监听端口。零表示由操作系统分配；固定端口需要调用者配置防火墙。</summary>
    public int ActivePort { get; init; }

    /// <summary>控制命令与目录文本编码。默认严格 UTF-8；旧服务器可显式设置 Latin1 等编码。</summary>
    public Encoding Encoding { get; init; } = new UTF8Encoding(false, true);

    /// <summary>服务器公布 UTF8/UTF-8 时是否尝试 OPTS UTF8 ON。只在编码为 UTF-8 时协商。</summary>
    public bool NegotiateUtf8 { get; init; } = true;

    /// <summary>建立 TCP 连接及 TLS 握手的最长时间，必须为正或 InfiniteTimeSpan。</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>单次命令写入和完整回复读取的最长时间，包含多行回复。</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>单次数据读取、写入或等待主动连接的最长空闲时间；持续传输不受总时长限制。</summary>
    public TimeSpan DataTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>传输复制缓冲区字节数，范围 1024 至 1048576。</summary>
    public int BufferSize { get; init; } = 65536;

    /// <summary>单条控制回复或目录文本行允许的最大字节数，防止无界内存增长。</summary>
    public int MaxLineLength { get; init; } = 16384;

    /// <summary>一次完整控制回复允许的最大字节数，包含 Telnet 协商字节。</summary>
    public int MaxReplyBytes { get; init; } = 1048576;

    /// <summary>一次控制回复允许的最大行数。</summary>
    public int MaxReplyLines { get; init; } = 1024;

    /// <summary>一次目录列表允许的最大字节数；文件传输使用流，不受此限制。</summary>
    public int MaxListingBytes { get; init; } = 16777216;

    /// <summary>一次目录列表允许的最大条目数。</summary>
    public int MaxListingEntries { get; init; } = 100000;

    /// <summary>递归目录操作的最大深度，防止异常目录树耗尽栈空间。</summary>
    public int MaxRecursionDepth { get; init; } = 64;

    /// <summary>递归删除或枚举允许访问的最大总条目数。</summary>
    public int MaxRecursiveEntries { get; init; } = 1000000;

    /// <summary>解析不含年份的 UNIX LIST 时间时使用的时间提供器，便于确定性测试。</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>校验参数并复制可变集合、编码，防止正在使用的连接配置被外部改变。</summary>
    internal FtpOptions Snapshot()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        if (Host.IndexOfAny(['\r', '\n', '\0', '/', '\\']) >= 0 || Host.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("Host 必须是 DNS 名称或 IP 地址。", nameof(Host));
        if (Port is < 0 or > 65535 || ActivePort is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), "端口必须位于 0 至 65535。");
        FtpPath.ValidateArgument(UserName, nameof(UserName), allowEmpty: true);
        FtpPath.ValidateArgument(Password, nameof(Password), allowEmpty: true);
        if (Account is not null) FtpPath.ValidateArgument(Account, nameof(Account), allowEmpty: true);
        ArgumentNullException.ThrowIfNull(Encoding);
        ArgumentNullException.ThrowIfNull(TransferSettings);
        ArgumentNullException.ThrowIfNull(TlsProvider);
        if (VirtualHost is not null) FtpPath.ValidateArgument(VirtualHost, nameof(VirtualHost));
        if (TlsTargetHost is not null) FtpPath.ValidateArgument(TlsTargetHost, nameof(TlsTargetHost));
        var transferSettings = TransferSettings.Snapshot();
        Security?.Validate();
        if (!Enum.IsDefined(TlsSessionReuse)) throw new ArgumentOutOfRangeException(nameof(TlsSessionReuse));
        const string asciiProbe = "\r\n !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~";
        if (!Encoding.GetBytes(asciiProbe).AsSpan().SequenceEqual(System.Text.Encoding.ASCII.GetBytes(asciiProbe)))
            throw new ArgumentException("FTP 控制通道编码必须兼容 ASCII，例如 UTF-8、Latin1 或已注册的 GBK。", nameof(Encoding));
        ArgumentNullException.ThrowIfNull(TimeProvider);
        ValidateTimeout(ConnectTimeout, nameof(ConnectTimeout));
        ValidateTimeout(CommandTimeout, nameof(CommandTimeout));
        ValidateTimeout(DataTimeout, nameof(DataTimeout));
        if (!Enum.IsDefined(Encryption)) throw new ArgumentOutOfRangeException(nameof(Encryption));
        if (!Enum.IsDefined(DataConnectionMode)) throw new ArgumentOutOfRangeException(nameof(DataConnectionMode));
        if (BufferSize is < 1024 or > 1048576) throw new ArgumentOutOfRangeException(nameof(BufferSize));
        if (MaxLineLength < 256 || MaxReplyBytes < MaxLineLength || MaxReplyLines < 1 ||
            MaxListingBytes < MaxLineLength || MaxListingEntries < 1 ||
            MaxRecursionDepth is < 1 or > 256 || MaxRecursiveEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxLineLength), "回复、列表和递归限制必须为有效的正数。");
        var encoding = (Encoding)Encoding.Clone();
        encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
        return this with
        {
            Encoding = encoding,
            TransferSettings = transferSettings,
            ClientCertificates = ClientCertificates is null ? null : new X509CertificateCollection(ClientCertificates)
        };
    }

    /// <summary>验证超时时间可由 CancellationTokenSource 表示；无限超时必须显式使用 InfiniteTimeSpan。</summary>
    private static void ValidateTimeout(TimeSpan value, string name)
    {
        if (value != Timeout.InfiniteTimeSpan && (value <= TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(name);
    }
}