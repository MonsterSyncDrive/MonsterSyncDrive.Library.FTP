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
using System.Net;
using System.Net.Security;
using System.Security.Authentication;

namespace MonsterSyncDrive.Library.FTP;

/// <summary>RFC 2228 控制命令的保护等级。Confidentiality 必须在 CCC 放宽完整性要求后才能选择。</summary>
public enum FtpCommandProtection
{
    /// <summary>不使用 RFC 2228 命令封装；控制连接本身仍可能受 TLS 保护。</summary>
    None,
    /// <summary>MIC：至少提供完整性和来源校验。</summary>
    Integrity,
    /// <summary>CONF：至少提供机密性；只能在成功 CCC 后使用。</summary>
    Confidentiality,
    /// <summary>ENC：同时要求完整性和机密性。</summary>
    Privacy
}

/// <summary>一个安全机制完成保护或解包后的消息，以及该机制实际提供的服务。</summary>
public sealed record FtpSecurityMessage
{
    /// <summary>机制输出的令牌，或者解包后的原始字节。结果必须拥有其缓冲区。</summary>
    public required ReadOnlyMemory<byte> Data
    {
        get; init;
    }

    /// <summary>此令牌是否经过完整性验证或签名。</summary>
    public required bool HasIntegrity
    {
        get; init;
    }

    /// <summary>此令牌是否经过加密。</summary>
    public required bool IsEncrypted
    {
        get; init;
    }
}

/// <summary>
/// 可插入的 AUTH/ADAT 安全关联。令牌格式与认证过程由机制决定，FTP 层只负责规范的 Base64 与状态机。
/// 一个实例只属于一个 FTP 会话，必须在认证成功后提供真实的完整性/机密性处理，不能将明文当作受保护消息。
/// </summary>
public interface IFtpSecurityMechanism : IDisposable
{
    /// <summary>AUTH 参数，例如 GSSAPI；只能包含 ASCII 字母、数字、下划线或连字符。</summary>
    string Name
    {
        get;
    }

    /// <summary>安全关联是否已经完成认证。</summary>
    bool IsAuthenticated
    {
        get;
    }

    /// <summary>机制是否支持消息完整性验证。</summary>
    bool SupportsIntegrity
    {
        get;
    }

    /// <summary>机制是否支持消息加密。</summary>
    bool SupportsConfidentiality
    {
        get;
    }

    /// <summary>检查机制是否定义此命令保护方式；机制专用规范可以禁止某些组合，例如标准 GSSAPI 不定义 CONF。</summary>
    bool SupportsCommandProtection(FtpCommandProtection protection)
    {
        return protection switch
        {
            FtpCommandProtection.None => true,
            FtpCommandProtection.Integrity => SupportsIntegrity,
            FtpCommandProtection.Confidentiality => SupportsConfidentiality,
            FtpCommandProtection.Privacy => SupportsIntegrity && SupportsConfidentiality,
            _ => false
        };
    }

    /// <summary>
    /// 根据服务器 challenge 生成下一个 ADAT 令牌。serverCompleted 为 true 时必须处理最终服务器令牌，
    /// 若仍需要向服务器发送内容，应返回非空令牌；FTP 层会拒绝与 235 不一致的额外交换。
    /// </summary>
    ReadOnlyMemory<byte> GetAuthenticationToken(ReadOnlyMemory<byte> challenge, bool serverCompleted);

    /// <summary>保护原始命令字节，不包含 Base64；返回实际应用的服务标志。</summary>
    FtpSecurityMessage Protect(ReadOnlyMemory<byte> message, FtpCommandProtection protection);

    /// <summary>校验、解密一个服务器令牌；认证失败必须抛异常，不能返回未经验证的字节。</summary>
    FtpSecurityMessage Unprotect(ReadOnlyMemory<byte> token, FtpCommandProtection protection);
}

/// <summary>在 USER/PASS 之前建立的通用安全关联配置。</summary>
public sealed record FtpSecurityOptions
{
    /// <summary>为每次新会话创建独立安全机制；重连时不重用旧认证上下文。</summary>
    public required Func<IFtpSecurityMechanism> MechanismFactory
    {
        get; init;
    }

    /// <summary>认证后自动使用的命令保护等级；默认 ENC，避免保护关联建立后仍发送明文凭据。</summary>
    public FtpCommandProtection CommandProtection { get; init; } = FtpCommandProtection.Privacy;

    /// <summary>AUTH/ADAT 往返次数上限，防止恶意服务器无限交换。</summary>
    public int MaxAuthenticationExchanges { get; init; } = 32;

    /// <summary>单个解码令牌以及解包后完整回复允许的最大字节数。</summary>
    public int MaxTokenBytes { get; init; } = 1048576;

    /// <summary>校验机制工厂、保护选择和资源限制。</summary>
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(MechanismFactory);
        if (!Enum.IsDefined(CommandProtection) || CommandProtection is FtpCommandProtection.None or FtpCommandProtection.Confidentiality)
            throw new ArgumentException("初始安全关联必须使用 MIC 或 ENC；CCC 后可显式放宽。", nameof(CommandProtection));
        if (MaxAuthenticationExchanges is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxAuthenticationExchanges));
        if (MaxTokenBytes is < 256 or > 16777216)
            throw new ArgumentOutOfRangeException(nameof(MaxTokenBytes));
    }
}

/// <summary>
/// 基于 .NET NegotiateAuthentication 的 GSSAPI 认证与消息保护。Package 可选择平台提供的 Kerberos、Negotiate 或 NTLM；
/// 是否可用取决于系统、票据及凭据，无法使用时保留明确的认证错误，不降级为伪保护。
/// </summary>
public sealed class FtpNegotiateSecurityMechanism : IFtpSecurityMechanism
{
    /// <summary>操作系统维护的 SSPI/GSSAPI 认证上下文。</summary>
    private readonly NegotiateAuthentication _authentication;

    /// <summary>使用显式的 SSPI/GSSAPI 客户端选项创建机制，不连接网络。</summary>
    /// <param name="options">目标 SPN、凭据、保护和互相认证选项；例如 TargetName 为 ftp/server.example.com。</param>
    /// <param name="mechanismName">FTP AUTH 标识。标准 GSSAPI 使用 GSSAPI。</param>
    public FtpNegotiateSecurityMechanism(NegotiateAuthenticationClientOptions options, string mechanismName = "GSSAPI")
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateMechanismName(mechanismName);
        _authentication = new NegotiateAuthentication(options);
        Name = mechanismName;
    }

    /// <summary>创建使用默认系统凭据和目标 SPN 的 GSSAPI 机制。</summary>
    /// <param name="targetName">目标服务主体名称，例如 ftp/server.example.com。</param>
    /// <param name="package">平台安全包，例如 Kerberos 或 Negotiate。</param>
    /// <param name="credential">显式凭据；为空时使用当前系统凭据。</param>
    public FtpNegotiateSecurityMechanism(string targetName, string package = "Kerberos", NetworkCredential? credential = null)
        : this(new NegotiateAuthenticationClientOptions
        {
            TargetName = targetName,
            Package = package,
            Credential = credential ?? CredentialCache.DefaultNetworkCredentials,
            RequiredProtectionLevel = ProtectionLevel.EncryptAndSign,
            RequireMutualAuthentication = true
        })
    {
    }

    /// <summary>AUTH 的机制名。</summary>
    public string Name
    {
        get;
    }

    /// <summary>底层上下文是否已经认证。</summary>
    public bool IsAuthenticated => _authentication.IsAuthenticated;

    /// <summary>认证上下文是否提供签名/完整性。</summary>
    public bool SupportsIntegrity => IsAuthenticated && _authentication.IsSigned;

    /// <summary>认证上下文是否提供加密。</summary>
    public bool SupportsConfidentiality => IsAuthenticated && _authentication.IsEncrypted;

    /// <summary>RFC 2228 Appendix I 明确不支持 GSSAPI 的 CONF/633；加密的 GSS_Wrap 应使用 ENC/632。</summary>
    public bool SupportsCommandProtection(FtpCommandProtection protection)
    {
        return protection switch
        {
            FtpCommandProtection.None => true,
            FtpCommandProtection.Integrity => SupportsIntegrity,
            FtpCommandProtection.Privacy => SupportsIntegrity && SupportsConfidentiality,
            _ => false
        };
    }

    /// <summary>调用 GSS_Init_sec_context/SSPI 生成令牌，并拒绝所有未完成/成功以外的状态。</summary>
    public ReadOnlyMemory<byte> GetAuthenticationToken(ReadOnlyMemory<byte> challenge, bool serverCompleted)
    {
        if (serverCompleted && challenge.IsEmpty && IsAuthenticated)
            return ReadOnlyMemory<byte>.Empty;
        byte[]? token = _authentication.GetOutgoingBlob(challenge.Span, out NegotiateAuthenticationStatusCode status);
        return status is not (NegotiateAuthenticationStatusCode.Completed or NegotiateAuthenticationStatusCode.ContinueNeeded)
            ? throw new AuthenticationException($"FTP GSSAPI 认证失败：{status}。")
            : serverCompleted && !IsAuthenticated ? throw new AuthenticationException("服务器完成了 ADAT，但本地安全关联尚未完成。") : (ReadOnlyMemory<byte>)(token ?? []);
    }

    /// <summary>使用真实系统安全上下文签名或加密命令，检查机制是否降级了要求的服务。</summary>
    public FtpSecurityMessage Protect(ReadOnlyMemory<byte> message, FtpCommandProtection protection)
    {
        if (!SupportsCommandProtection(protection) || protection == FtpCommandProtection.None)
            throw new NotSupportedException("GSSAPI 仅定义 MIC/ENC 消息保护，不定义 CONF。请使用 ENC。 ");
        var output = new ArrayBufferWriter<byte>();
        NegotiateAuthenticationStatusCode status = _authentication.Wrap(message.Span, output, protection is FtpCommandProtection.Privacy or FtpCommandProtection.Confidentiality,
            out bool encrypted);
        return status != NegotiateAuthenticationStatusCode.Completed
            ? throw new AuthenticationException($"FTP 消息保护失败：{status}。")
            : new FtpSecurityMessage { Data = output.WrittenMemory.ToArray(), HasIntegrity = _authentication.IsSigned, IsEncrypted = encrypted };
    }

    /// <summary>使用上下文验证序号/签名并解密，任何验证错误都失败关闭。</summary>
    public FtpSecurityMessage Unprotect(ReadOnlyMemory<byte> token, FtpCommandProtection protection)
    {
        if (!SupportsCommandProtection(protection) || protection == FtpCommandProtection.None)
            throw new NotSupportedException("GSSAPI 仅定义 631/632 消息保护，不定义 633。 ");
        var output = new ArrayBufferWriter<byte>();
        NegotiateAuthenticationStatusCode status = _authentication.Unwrap(token.Span, output, out bool encrypted);
        return status != NegotiateAuthenticationStatusCode.Completed
            ? throw new AuthenticationException($"FTP 消息验证失败：{status}。")
            : new FtpSecurityMessage { Data = output.WrittenMemory.ToArray(), HasIntegrity = _authentication.IsSigned, IsEncrypted = encrypted };
    }

    /// <summary>释放系统安全上下文与相关凭据句柄。</summary>
    public void Dispose()
    {
        _authentication.Dispose();
    }

    /// <summary>禁止控制字符或参数注入进入 AUTH 机制名。</summary>
    internal static void ValidateMechanismName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 64 || value.Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new ArgumentException("AUTH 机制名只能包含 ASCII 字母、数字、下划线和连字符。", nameof(value));
    }
}