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
using System.Globalization;
using System.Net.Security;
using System.Security.Authentication;

namespace MonsterSyncDrive.FTP;

/// <summary>RFC 2228 安全关联、受保护命令、TLS 会话策略以及控制通道降级。</summary>
public sealed partial class FtpClient
{
    /// <summary>当前认证的通用安全机制；只在 AUTH/ADAT 全部验证完成后发布。</summary>
    private IFtpSecurityMechanism? _securityMechanism;

    /// <summary>当前控制命令的消息保护等级。</summary>
    private FtpCommandProtection _commandProtection;

    /// <summary>当前安全关联的资源限制。</summary>
    private FtpSecurityOptions? _securityOptions;

    /// <summary>当前关联是否已经通过 CCC 放宽了消息完整性要求。</summary>
    private bool _commandProtectionCleared;

    /// <summary>原始控制 TCP 流；控制 TLS 始终以 leaveOpen 包装，以便 CCC/REIN 可以降级。</summary>
    private Stream? _controlTransport;

    /// <summary>当前有效的控制 TLS 会话。</summary>
    private FtpTlsSession? _controlTlsSession;

    /// <summary>CCC 后已结束但仍保留恢复元数据的控制会话。</summary>
    private FtpTlsSession? _retiredControlTlsSession;

    /// <summary>最近一次数据 TLS 认证结果。</summary>
    private FtpTlsSession? _dataTlsSession;

    /// <summary>最近控制握手的恢复观测快照，独立于包装流是否已经清理。</summary>
    private bool? _lastControlTlsSessionResumed;

    /// <summary>最近数据握手的恢复观测快照，数据传输完成释放资源后仍可查询。</summary>
    private bool? _lastDataTlsSessionResumed;

    /// <summary>最近数据握手是否经提供器验证绑定到控制会话；null 始终代表未知。</summary>
    private bool? _lastDataTlsControlSessionReused;

    /// <summary>当前 AUTH 安全机制名；未建立通用安全关联时为空。</summary>
    public string? SecurityMechanismName => _securityMechanism?.Name;

    /// <summary>当前 RFC 2228 命令保护等级；TLS 保护与此属性相互独立。</summary>
    public FtpCommandProtection CommandProtection => _commandProtection;

    /// <summary>控制 TLS 握手是否恢复过旧会话；默认平台提供器不能观测，返回空。</summary>
    public bool? ControlTlsSessionResumed => _lastControlTlsSessionResumed;

    /// <summary>最近数据握手是否恢复旧会话；未知时为空，不把允许恢复当作恢复成功。</summary>
    public bool? DataTlsSessionResumed => _lastDataTlsSessionResumed;

    /// <summary>最近数据握手是否已被提供器验证为复用指定控制会话；未知时为空。</summary>
    public bool? DataTlsControlSessionReused => _lastDataTlsControlSessionReused;

    /// <summary>最近数据握手的恢复结果快照；对应流已经释放或会话已关闭时仍保留最后观测结果。</summary>
    public bool? LastDataTlsSessionResumed => _lastDataTlsSessionResumed;

    /// <summary>最近数据握手的控制绑定结果快照；未知不推测成 true，严格策略失败的 false 也保留。</summary>
    public bool? LastDataTlsControlSessionReused => _lastDataTlsControlSessionReused;

    /// <summary>切换 MIC/ENC，或在成功 CCC 后切换 CONF/未封装；不会削弱未放宽的完整性要求。</summary>
    public Task SetCommandProtectionAsync(FtpCommandProtection protection, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(protection)) throw new ArgumentOutOfRangeException(nameof(protection));
        return RunAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            var mechanism = _securityMechanism ?? throw new InvalidOperationException("尚未建立 AUTH/ADAT 安全关联。");
            if (protection is FtpCommandProtection.None or FtpCommandProtection.Confidentiality && !_commandProtectionCleared)
                throw new InvalidOperationException("必须先成功执行 CCC 才能放宽命令完整性要求。");
            ValidateMechanismServices(mechanism, protection);
            _commandProtection = protection;
            return Task.CompletedTask;
        }, cancellationToken);
    }

    /// <summary>
    /// 通过 CCC 清除控制通道 TLS/消息保护，同时保留当前数据通道 PROT 设置。
    /// 拒绝 CCC 的最终回复不会改变现有保护；关闭通知、认证或缓冲状态异常会关闭会话。
    /// </summary>
    public Task ClearCommandChannelAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            if (_controlTlsSession is null && _securityMechanism is null)
                throw new InvalidOperationException("控制通道没有可清除的安全关联。");
            Expect("CCC", await CommandAsync("CCC", null, token).ConfigureAwait(false), 200);
            // RFC 2228 要求 CCC 自身仍有完整性保护；只有确认 200 后才能放宽后续消息。
            _commandProtection = FtpCommandProtection.None;
            _commandProtectionCleared = true;
            if (_controlTlsSession is not null) await DowngradeControlTlsCoreAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>在 USER/PASS 前完成 AUTH/ADAT，包括最终服务器令牌与双端完成状态校验。</summary>
    private async Task AuthenticateSecurityCoreAsync(CancellationToken cancellationToken)
    {
        var options = _options.Security;
        if (options is null) return;
        options.Validate();
        var mechanism = options.MechanismFactory() ?? throw new InvalidOperationException("安全机制工厂返回了空值。");
        try
        {
            FtpNegotiateSecurityMechanism.ValidateMechanismName(mechanism.Name);
            var reply = await CommandAsync("AUTH", mechanism.Name, cancellationToken).ConfigureAwait(false);
            Expect("AUTH", reply, 234, 334);
            for (var exchange = 0; exchange < options.MaxAuthenticationExchanges; exchange++)
            {
                var completed = reply.Code is 234 or 235;
                var incoming = DecodeAdat(reply, options.MaxTokenBytes);
                var outgoing = mechanism.GetAuthenticationToken(incoming, completed);
                if (outgoing.Length > options.MaxTokenBytes) throw new FtpProtocolException("AUTH/ADAT 令牌超过资源限制。");
                if (completed)
                {
                    if (!mechanism.IsAuthenticated || !outgoing.IsEmpty)
                        throw new AuthenticationException("FTP 服务端和本地安全机制的完成状态不一致。");
                    ValidateMechanismServices(mechanism, options.CommandProtection);
                    _securityOptions = options;
                    _commandProtection = options.CommandProtection;
                    _commandProtectionCleared = false;
                    _securityMechanism = mechanism;
                    return;
                }
                // 335 可以携带空 challenge，但不能让本地已完成状态跳过服务器的最终 235。
                // 即使机制的下一令牌为空，也必须显式发送空 ADAT 以完成服务器要求的这一步。
                reply = await CommandAsync("ADAT", Convert.ToBase64String(outgoing.Span), cancellationToken).ConfigureAwait(false);
                Expect("ADAT", reply, 235, 335);
            }
            throw new FtpProtocolException("AUTH/ADAT 交换次数超过资源限制。");
        }
        catch { mechanism.Dispose(); throw; }
    }

    /// <summary>根据当前关联包装命令。调用者必须已经持有控制通道锁，本方法不会重复申请锁。</summary>
    private Task SendSecuredCommandAsync(FtpControlChannel channel, string verb, string? argument, CancellationToken cancellationToken)
    {
        var mechanism = _securityMechanism;
        var protection = _commandProtection;
        if (mechanism is null || protection == FtpCommandProtection.None)
            return channel.SendAsync(verb, argument, cancellationToken);
        var text = (argument is null ? verb : verb + " " + argument) + "\r\n";
        var message = mechanism.Protect(_options.Encoding.GetBytes(text), protection);
        ValidateProtectedMessage(message, protection);
        if (message.Data.Length > _securityOptions!.MaxTokenBytes) throw new FtpProtocolException("受保护命令超过令牌字节限制。");
        var outer = protection switch
        {
            FtpCommandProtection.Integrity => "MIC",
            FtpCommandProtection.Confidentiality => "CONF",
            _ => "ENC"
        };
        return channel.SendAsync(outer, Convert.ToBase64String(message.Data.Span), cancellationToken);
    }

    /// <summary>读取并验证 631/632/633 回复，阻止保护降级、嵌套封装、令牌篡改与回复拼接。</summary>
    private async Task<FtpReply> ReadSecuredReplyAsync(FtpControlChannel channel, CancellationToken cancellationToken)
    {
        var reply = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
        var mechanism = _securityMechanism;
        if (reply.Code is not (631 or 632 or 633))
        {
            if (reply.Code >= 600) throw new FtpProtocolException("服务器返回了未知的受保护回复类型。");
            // AUTH/ADAT 的安全关联尚未发布时仍可接收原始回复。关联启用后不接受裸露的成功/错误回复，
            // 否则攻击者能够注入 230/226 或伪造服务器错误。RFC 2228 也要求机制错误回复受保护。
            if (mechanism is not null && _commandProtection != FtpCommandProtection.None)
                throw new AuthenticationException("服务器将受保护回复降级为未验证的明文回复。");
            return reply;
        }
        if (mechanism is null) throw new AuthenticationException("收到受保护回复但没有安全关联。");
        var protection = reply.Code switch
        {
            631 => FtpCommandProtection.Integrity,
            632 => FtpCommandProtection.Privacy,
            _ => FtpCommandProtection.Confidentiality
        };
        if ((_commandProtection is FtpCommandProtection.Integrity or FtpCommandProtection.Privacy) && reply.Code == 633)
            throw new AuthenticationException("服务器回复缺少要求的完整性保护。");
        if ((_commandProtection is FtpCommandProtection.Privacy or FtpCommandProtection.Confidentiality) && reply.Code == 631)
            throw new AuthenticationException("服务器回复缺少要求的机密性保护。");

        var maximum = _securityOptions?.MaxTokenBytes ?? _options.MaxReplyBytes;
        using var plaintext = new MemoryStream();
        var expected = reply.Code.ToString(CultureInfo.InvariantCulture);
        for (var index = 0; index < reply.Lines.Count; index++)
        {
            var line = reply.Lines[index];
            // 每个外层多行片段都是独立的机制令牌，不能先拼接 Base64 再校验一次。
            if (line.Length < 5 || !line.StartsWith(expected, StringComparison.Ordinal) ||
                line[3] != (index == reply.Lines.Count - 1 ? ' ' : '-'))
                throw new FtpProtocolException("受保护多行回复没有按相同代码逐行封装。");
            var token = DecodeBase64(line[4..], maximum, "受保护回复");
            var message = mechanism.Unprotect(token, protection);
            ValidateProtectedMessage(message, protection);
            if (plaintext.Length + message.Data.Length > maximum) throw new FtpProtocolException("解包回复超过资源限制。");
            plaintext.Write(message.Data.Span);
        }
        return ParseProtectedPlaintext(plaintext.ToArray());
    }

    /// <summary>解析经过认证的完整回复，允许可选末尾 CRLF，但不接受第二个独立回复或嵌套 6xx。</summary>
    private FtpReply ParseProtectedPlaintext(byte[] bytes)
    {
        var text = _options.Encoding.GetString(bytes);
        if (text.EndsWith("\r\n", StringComparison.Ordinal)) text = text[..^2];
        if (text.Length == 0 || text.Replace("\r\n", "", StringComparison.Ordinal).IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new FtpProtocolException("受保护回复包含无效分帧。");
        var lines = text.Split("\r\n", StringSplitOptions.None);
        if (lines.Length > _options.MaxReplyLines) throw new FtpProtocolException("解包回复超过行数限制。");
        var first = lines[0];
        if (first.Length < 3 || !int.TryParse(first.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var code) ||
            code is < 100 or > 599 || (first.Length > 3 && first[3] is not (' ' or '-')))
            throw new FtpProtocolException("受保护消息没有包含有效的普通 FTP 回复。");
        foreach (var line in lines)
            if (_options.Encoding.GetByteCount(line) > _options.MaxLineLength) throw new FtpProtocolException("解包回复行超过长度限制。");
        if (first.Length > 3 && first[3] == '-')
        {
            var endPrefix = first[..3] + " ";
            if (lines.Length < 2 || !lines[^1].StartsWith(endPrefix, StringComparison.Ordinal) ||
                lines.Skip(1).SkipLast(1).Any(line => line.StartsWith(endPrefix, StringComparison.Ordinal)))
                throw new FtpProtocolException("受保护多行回复的结束标记无效。");
        }
        else if (lines.Length != 1) throw new FtpProtocolException("受保护令牌拼接了多个独立回复。");
        return new FtpReply(code, lines);
    }

    /// <summary>对控制和数据 TLS 采用同一证书策略，并精确执行调用者指定的恢复要求。</summary>
    private async Task<Stream> AuthenticateTlsStreamAsync(Stream transport, bool isDataChannel, CancellationToken cancellationToken)
    {
        if (isDataChannel) { _lastDataTlsSessionResumed = null; _lastDataTlsControlSessionReused = null; }
        else { _lastControlTlsSessionResumed = null; _lastDataTlsSessionResumed = null; _lastDataTlsControlSessionReused = null; }
        var authentication = new SslClientAuthenticationOptions
        {
            TargetHost = _options.TlsTargetHost ?? SelectedVirtualHost ?? _options.VirtualHost ?? _options.Host,
            EnabledSslProtocols = _options.EnabledSslProtocols,
            CertificateRevocationCheckMode = _options.CertificateRevocationCheckMode,
            ClientCertificates = _options.ClientCertificates,
            RemoteCertificateValidationCallback = _options.CertificateValidation,
            AllowTlsResume = _options.TlsSessionReuse != FtpTlsSessionReusePolicy.Disabled
        };
        var session = await FtpTimeout.RunAsync(token => _options.TlsProvider.AuthenticateAsync(new FtpTlsAuthenticationContext
        {
            Transport = transport,
            AuthenticationOptions = authentication,
            IsDataChannel = isDataChannel,
            ControlSession = isDataChannel ? _controlTlsSession ?? _retiredControlTlsSession : null,
            LeaveTransportOpen = !isDataChannel
        }, token), _options.ConnectTimeout, cancellationToken, "TLS 握手").ConfigureAwait(false);
        if (session is null) throw new AuthenticationException("TLS 提供器返回了空会话。");
        if (isDataChannel)
        {
            _lastDataTlsSessionResumed = session.IsResumed;
            _lastDataTlsControlSessionReused = session.IsControlSessionReused;
        }
        else _lastControlTlsSessionResumed = session.IsResumed;
        if (isDataChannel && _options.TlsSessionReuse == FtpTlsSessionReusePolicy.RequireControlSessionReuse &&
            session.IsControlSessionReused != true)
        {
            session.Dispose();
            if (session.IsControlSessionReused is null)
                throw new NotSupportedException("TLS 提供器不能验证数据连接是否复用指定控制会话；默认 SslStream 不公开此能力。");
            throw new AuthenticationException("TLS 数据连接没有复用要求的控制会话。");
        }
        if (isDataChannel) { _dataTlsSession?.Dispose(); _dataTlsSession = session; }
        else
        {
            _retiredControlTlsSession?.Dispose(); _retiredControlTlsSession = null;
            _controlTlsSession?.Dispose(); _controlTlsSession = session;
        }
        return session.Stream;
    }

    /// <summary>只执行 TLS 关闭与换流，不发送命令。供已获成功回复的 CCC 和 REIN 共用。</summary>
    private async Task DowngradeControlTlsCoreAsync(CancellationToken cancellationToken)
    {
        var session = _controlTlsSession;
        if (session is null) return;
        var transport = _controlTransport ?? throw new FtpProtocolException("缺少可恢复的原始控制传输。");
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FtpTimeout.RunAsync(async token =>
            {
                await session.ShutdownAsync(token).ConfigureAwait(false);
                // 读取对端 close_notify，防止把 TLS 记录残留交给明文分帧器。
                // 服务器按规范等待下一条明文命令，因此这里不能出现应用数据。
                var one = new byte[1];
                if (await session.Stream.ReadAsync(one, token).ConfigureAwait(false) != 0)
                    throw new FtpProtocolException("TLS 清除时出现未消费的控制数据。");
            }, _options.CommandTimeout, cancellationToken, "控制 TLS 关闭").ConfigureAwait(false);
            await session.Stream.DisposeAsync().ConfigureAwait(false);
            _retiredControlTlsSession?.Dispose();
            _retiredControlTlsSession = session;
            _controlTlsSession = null;
            _controlStream = transport;
            _channel = new FtpControlChannel(transport, _options);
        }
        finally { _controlGate.Release(); }
    }

    /// <summary>清除认证上下文；可选择保留控制 TLS 传输，供会话重置的分阶段处理使用。</summary>
    private void ResetSecurityState(bool keepTlsTransport = false)
    {
        Interlocked.Exchange(ref _securityMechanism, null)?.Dispose();
        _securityOptions = null; _commandProtection = FtpCommandProtection.None; _commandProtectionCleared = false;
        Interlocked.Exchange(ref _dataTlsSession, null)?.Dispose();
        if (!keepTlsTransport)
        {
            Interlocked.Exchange(ref _controlTlsSession, null)?.Dispose();
            Interlocked.Exchange(ref _retiredControlTlsSession, null)?.Dispose();
            _controlTransport = null;
        }
    }

    /// <summary>提取服务器可选的 ADAT= 令牌，拒绝畸形 Base64 和超限数据。</summary>
    private static byte[] DecodeAdat(FtpReply reply, int maximum)
    {
        var message = reply.Message.Trim();
        if (!message.StartsWith("ADAT=", StringComparison.OrdinalIgnoreCase)) return [];
        var token = message[5..].Split(' ', 2)[0];
        return DecodeBase64(token, maximum, "ADAT");
    }

    /// <summary>先检查编码长度预算再解码，以避免超大 Base64 输入分配。</summary>
    private static byte[] DecodeBase64(string text, int maximum, string description)
    {
        if (text.Length > ((long)maximum + 2) / 3 * 4 || text.Any(char.IsWhiteSpace))
            throw new FtpProtocolException(description + " 令牌超限或包含非法空白。");
        byte[] token;
        try { token = Convert.FromBase64String(text); }
        catch (FormatException) { throw new FtpProtocolException(description + " 令牌不是有效 Base64。"); }
        if (token.Length > maximum) throw new FtpProtocolException(description + " 令牌超过字节限制。");
        return token;
    }

    /// <summary>确保安全机制能够满足请求的服务，而不是默认信任其类型名称。</summary>
    private static void ValidateMechanismServices(IFtpSecurityMechanism mechanism, FtpCommandProtection protection)
    {
        if (!mechanism.SupportsCommandProtection(protection))
            throw new NotSupportedException("该安全机制没有定义请求的命令保护方式。 ");
        if (protection is FtpCommandProtection.Integrity or FtpCommandProtection.Privacy && !mechanism.SupportsIntegrity)
            throw new AuthenticationException("安全机制不提供所需的完整性服务。");
        if (protection is FtpCommandProtection.Confidentiality or FtpCommandProtection.Privacy && !mechanism.SupportsConfidentiality)
            throw new AuthenticationException("安全机制不提供所需的机密性服务。");
    }

    /// <summary>验证实际消息保护结果，拒绝机制在单次 Wrap/Unwrap 中发生的降级。</summary>
    private static void ValidateProtectedMessage(FtpSecurityMessage message, FtpCommandProtection protection)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (protection is FtpCommandProtection.Integrity or FtpCommandProtection.Privacy && !message.HasIntegrity)
            throw new AuthenticationException("消息缺少要求的完整性保护。");
        if (protection is FtpCommandProtection.Confidentiality or FtpCommandProtection.Privacy && !message.IsEncrypted)
            throw new AuthenticationException("消息缺少要求的加密保护。");
    }
}