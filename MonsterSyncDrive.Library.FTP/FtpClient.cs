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

using MonsterSyncDrive.Library.FTP.Protocol;
using System.Collections.ObjectModel;
using System.Net.Sockets;

namespace MonsterSyncDrive.Library.FTP;

/// <summary>
/// 跨平台、支持 Native AOT 的异步 FTP/FTPS 客户端。不依赖 FtpWebRequest、反射、动态代码或第三方包。
/// 一个实例串行使用一个控制会话；并行传输请使用多个客户端。文件流在完成或释放前独占会话。
/// </summary>
public sealed partial class FtpClient : IDisposable, IAsyncDisposable
{
    /// <summary>构造时复制的只读会话参数。</summary>
    private readonly FtpOptions _options;

    /// <summary>FTP 控制会话只能顺序执行命令；数据流完成前保持此锁。</summary>
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    /// <summary>释放客户端时取消所有正在等待或运行的网络操作。</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>控制连接 TCP 客户端。</summary>
    private TcpClient? _controlClient;

    /// <summary>控制连接的 NetworkStream 或 SslStream。</summary>
    private Stream? _controlStream;

    /// <summary>控制回复分帧器。</summary>
    private FtpControlChannel? _channel;

    /// <summary>当前活动数据连接，供释放客户端时中断传输。</summary>
    private TcpClient? _dataClient;

    /// <summary>当前数据流，可能已套上 TLS。</summary>
    private Stream? _dataStream;

    /// <summary>是否已发送预备命令、REST 或 RNFR，且尚未完成对应协议事务。</summary>
    private bool _pendingSequence;

    /// <summary>已成功登录的状态标记；不主动探测远端是否已断线。</summary>
    private int _connected;

    /// <summary>线程安全的释放标记。</summary>
    private int _disposed;

    /// <summary>根据独立配置创建客户端；此时不会联网。</summary>
    /// <param name="options">服务器、凭据、加密、超时及资源限制。</param>
    public FtpClient(FtpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Snapshot();
    }

    /// <summary>最近一次已知的登录状态。使用 NoopAsync 可探测会话；断线不会自动重试有副作用的操作。</summary>
    public bool IsConnected => Volatile.Read(ref _connected) != 0 && Volatile.Read(ref _disposed) == 0;

    /// <summary>当前控制连接是否成功启用 TLS。</summary>
    public bool IsEncrypted => IsTransportConnected && _controlTlsSession is not null;

    /// <summary>控制 TCP 会话仍存在，即使 REIN 后尚未重新登录也返回 true。</summary>
    public bool IsTransportConnected => _channel is not null && Volatile.Read(ref _disposed) == 0;

    /// <summary>当前控制会话是否已经完成用户登录。</summary>
    public bool IsAuthenticated => IsConnected;

    /// <summary>服务器通过 FEAT 公布的特性快照；未公布不一定表示不支持。</summary>
    public IReadOnlyList<string> Features { get; private set; } = Array.Empty<string>();

    /// <summary>建立连接、完成可选 TLS、登录、能力发现并选择二进制传输。重复连接已连接实例没有副作用。</summary>
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(ConnectCoreAsync, cancellationToken, requireConnected: false);
    }

    /// <summary>在 REIN 保留的 TCP 连接上重新协商安全与登录；没有连接时建立新连接。</summary>
    public Task LoginAsync(CancellationToken cancellationToken = default)
    {
        return ConnectAsync(cancellationToken);
    }

    /// <summary>使用指定身份在未认证的控制连接登录；可在 REIN 后切换用户，密码不会写入异常消息。</summary>
    /// <param name="userName">本次登录用户名，不修改客户端构造配置。</param>
    /// <param name="password">本次登录密码。</param>
    /// <param name="account">服务器要求 332 时提供的账户。</param>
    /// <param name="cancellationToken">取消本次排队、建立连接或认证。</param>
    public Task LoginAsync(string userName, string password, string? account = null, CancellationToken cancellationToken = default)
    {
        FtpPath.ValidateArgument(userName, nameof(userName), allowEmpty: true);
        FtpPath.ValidateArgument(password, nameof(password), allowEmpty: true);
        if (account is not null)
            FtpPath.ValidateArgument(account, nameof(account), allowEmpty: true);
        return RunAsync(token => ConnectWithCredentialsCoreAsync(token, userName, password, account),
            cancellationToken, requireConnected: false, requireUnauthenticated: true);
    }

    /// <summary>主动关闭旧会话后重新登录。不会自动恢复工作目录或重放之前的操作。</summary>
    public Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(async token =>
        {
            CloseConnection();
            await ConnectCoreAsync(token).ConfigureAwait(false);
        },
            cancellationToken, requireConnected: false);
    }

    /// <summary>顺序发送 QUIT 并关闭连接；网络失败时仍保证释放套接字。</summary>
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(async token =>
        {
            try
            {
                if (IsConnected)
                    Expect("QUIT", await CommandAsync("QUIT", null, token).ConfigureAwait(false), 221);
            }
            finally
            {
                CloseConnection();
            }
        }, cancellationToken, requireConnected: false);
    }

    /// <summary>发送 NOOP 探测控制连接；不会自动重连。</summary>
    public Task NoopAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(async token => { Expect("NOOP", await CommandAsync("NOOP", null, token).ConfigureAwait(false), 200); }, cancellationToken);
    }

    /// <summary>查询服务器系统说明。</summary>
    public Task<string> GetSystemAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(async token =>
        {
            FtpReply reply = await CommandAsync("SYST", null, token).ConfigureAwait(false);
            Expect("SYST", reply, 215);
            return reply.Message;
        }, cancellationToken);
    }

    /// <summary>判断 FEAT 是否公布指定特性动词，不区分大小写。</summary>
    public bool SupportsFeature(string feature)
    {
        FtpPath.ValidateArgument(feature, nameof(feature));
        return Features.Any(line => line.Equals(feature, StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith(feature + " ", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 执行无需数据通道且不改变会话协议状态的扩展命令，例如 HELP、STAT、SITE。
    /// 返回包含错误码的最终回复；登录、编码、传输、端点及多步命令必须使用专用 API。
    /// </summary>
    /// <param name="command">仅含 ASCII 字母的命令动词。</param>
    /// <param name="argument">原始参数，不作 URI 编码；禁止控制字符。</param>
    /// <param name="cancellationToken">取消本次排队或执行。</param>
    public Task<FtpReply> ExecuteAsync(string command, string? argument = null, CancellationToken cancellationToken = default)
    {
        FtpPath.ValidateArgument(command, nameof(command));
        if (command.Length > 16 || command.Any(static c => !char.IsAsciiLetter(c)))
            throw new ArgumentException("命令动词只能包含 ASCII 字母，且最长 16 字符。", nameof(command));
        string verb = command.ToUpperInvariant();
        if (verb is "AUTH" or "USER" or "PASS" or "ACCT" or "PBSZ" or "PROT" or "CCC" or "REIN" or "QUIT" or
            "TYPE" or "MODE" or "STRU" or "REST" or "RETR" or "STOR" or "APPE" or "STOU" or "MLSD" or
            "LIST" or "NLST" or "RNFR" or "RNTO" or "EPSV" or "PASV" or "EPRT" or "PORT" or "LPRT" or "LPSV" or
            "ABOR" or "OPTS" or "HOST" or "ADAT" or "MIC" or "CONF" or "ENC")
            throw new ArgumentException("此命令需要专用 FTP API，以维护会话同步。", nameof(command));
        if (argument is not null)
            FtpPath.ValidateArgument(argument, nameof(argument), allowEmpty: true);
        return RunAsync(async token =>
        {
            FtpReply reply = await CommandAsync(verb, argument, token).ConfigureAwait(false);
            return reply.IsPreliminary || reply.IsIntermediate ? throw new FtpProtocolException("扩展命令没有返回最终回复，会话已关闭。") : reply;
        }, cancellationToken);
    }

    /// <summary>建立独占会话，包括 120 延迟欢迎、AUTH TLS、USER/PASS/ACCT 和 FEAT 兼容处理。</summary>
    private Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        return ConnectWithCredentialsCoreAsync(cancellationToken, _options.UserName, _options.Password, _options.Account);
    }

    /// <summary>在新建或 REIN 保留的连接上，按 HOST、安全机制、用户身份及传输参数的顺序重新初始化。</summary>
    private async Task ConnectWithCredentialsCoreAsync(CancellationToken cancellationToken, string userName, string password, string? account)
    {
        if (IsConnected)
            return;
        if (!IsTransportConnected)
        {
            CloseConnection();
            var client = new TcpClient { NoDelay = true };
            _controlClient = client;
            await FtpTimeout.RunAsync(token => client.ConnectAsync(_options.Host,
                _options.Port == 0 ? (_options.Encryption == FtpEncryptionMode.ImplicitTls ? 990 : 21) : _options.Port, token).AsTask(),
                _options.ConnectTimeout, cancellationToken, "连接").ConfigureAwait(false);
            _controlTransport = client.GetStream();
            _controlStream = _controlTransport;
            if (_options.Encryption == FtpEncryptionMode.ImplicitTls)
                _controlStream = await AuthenticateTlsStreamAsync(_controlStream, false, cancellationToken).ConfigureAwait(false);
            _channel = new FtpControlChannel(_controlStream, _options);
            FtpReply greeting = await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
            if (greeting.Code == 120)
                greeting = await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
            Expect("CONNECT", greeting, 220);
        }
        if (_options.VirtualHost is not null && SelectedVirtualHost is null)
            await SelectVirtualHostCoreAsync(_options.VirtualHost, cancellationToken).ConfigureAwait(false);
        // REIN 后即使原连接使用隐式 TLS，也已按 RFC4217 降为明文；重新登录使用 AUTH TLS 恢复保护。
        if (_options.Encryption != FtpEncryptionMode.None && !IsEncrypted)
        {
            Expect("AUTH", await CommandAsync("AUTH", "TLS", cancellationToken).ConfigureAwait(false), 234);
            // AUTH 成功后服务器等待 TLS ClientHello；不发送明文密码，也不在握手失败时降级。
            _controlStream = await AuthenticateTlsStreamAsync(_controlStream ?? throw new ObjectDisposedException(nameof(FtpClient)),
                false, cancellationToken).ConfigureAwait(false);
            _channel = new FtpControlChannel(_controlStream, _options);
        }
        await AuthenticateSecurityCoreAsync(cancellationToken).ConfigureAwait(false);
        FtpReply login = await CommandAsync("USER", userName, cancellationToken).ConfigureAwait(false);
        if (login.Code == 331)
            login = await CommandAsync("PASS", password, cancellationToken).ConfigureAwait(false);
        if (login.Code == 332)
        {
            if (account is null)
                throw new FtpCommandException("ACCT", login);
            login = await CommandAsync("ACCT", account, cancellationToken).ConfigureAwait(false);
        }
        Expect("LOGIN", login, 230, 232, 202);
        if (_options.Encryption != FtpEncryptionMode.None)
        {
            Expect("PBSZ", await CommandAsync("PBSZ", "0", cancellationToken).ConfigureAwait(false), 200);
            Expect("PROT", await CommandAsync("PROT", _options.EncryptDataChannel ? "P" : "C", cancellationToken).ConfigureAwait(false), 200);
            _dataChannelTls = _options.EncryptDataChannel;
        }

        FtpReply features = await CommandAsync("FEAT", null, cancellationToken).ConfigureAwait(false);
        if (features.Code == 211)
        {
            // 单行 211 仅表示没有扩展；多行回复的首尾行都是说明，不是能力。
            Features = new ReadOnlyCollection<string>(features.Lines.Skip(1).SkipLast(1)
                .Select(static line => line.Trim()).Where(static line => line.Length > 0).ToArray());
        }
        else if (!IsUnsupported(features))
            throw new FtpCommandException("FEAT", features);
        if (_options.NegotiateUtf8 && _options.Encoding.CodePage == 65001 &&
            (SupportsFeature("UTF8") || SupportsFeature("UTF-8")))
        {
            FtpReply utf8 = await CommandAsync("OPTS", "UTF8 ON", cancellationToken).ConfigureAwait(false);
            if (utf8.Code is not (200 or 202) && !IsUnsupported(utf8))
                throw new FtpCommandException("OPTS", utf8);
        }
        await ApplyTransferSettingsCoreAsync(_options.TransferSettings, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _connected, 1);
    }

    /// <summary>写入命令并读取一个回复；同一超时覆盖两个阶段和所有多行内容。</summary>
    private Task<FtpReply> CommandAsync(string verb, string? argument, CancellationToken cancellationToken)
    {
        return FtpTimeout.RunAsync(async token =>
        {
            await _controlGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // 保留当前通道引用；Dispose 可以在任意线程关闭字段，但不能让此处在两次 await 之间空引用。
                FtpControlChannel channel = _channel ?? throw new ObjectDisposedException(nameof(FtpClient), "控制连接已关闭。");
                await SendSecuredCommandAsync(channel, verb, argument, token).ConfigureAwait(false);
                FtpReply reply = await ReadSecuredReplyAsync(channel, token).ConfigureAwait(false);
                while (reply.Code == 110)
                {
                    ProcessRestartMarkerReply(reply);
                    reply = await ReadSecuredReplyAsync(channel, token).ConfigureAwait(false);
                }
                if (reply.Code == 421)
                {
                    CloseConnection();
                    throw new FtpCommandException(verb, reply);
                }
                return reply;
            }
            finally
            {
                _ = _controlGate.Release();
            }
        }, _options.CommandTimeout, cancellationToken, verb + " 命令");
    }

    /// <summary>读取服务器主动回复或数据传输完成回复。</summary>
    private Task<FtpReply> ReadReplyAsync(CancellationToken cancellationToken)
    {
        return FtpTimeout.RunAsync(async token =>
        {
            await _controlGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await ReadSecuredReplyAsync(_channel ?? throw new ObjectDisposedException(nameof(FtpClient)), token).ConfigureAwait(false);
            }
            finally
            {
                _ = _controlGate.Release();
            }
        }, _options.CommandTimeout, cancellationToken, "回复读取");
    }

    /// <summary>断言回复码属于预期集合；异常消息仅包含动词和状态码。</summary>
    private static void Expect(string verb, FtpReply reply, params int[] accepted)
    {
        if (!accepted.Contains(reply.Code))
            throw new FtpCommandException(verb, reply);
    }

    /// <summary>只有明确的未实现/语法不支持回复才允许兼容回退，不能把权限错误当成不支持。</summary>
    private static bool IsUnsupported(FtpReply reply)
    {
        return reply.Code is 500 or 501 or 502 or 504 or 522;
    }

    /// <summary>只有完整的最终回复且没有待续事务时才可继续复用连接。</summary>
    private bool CanKeepConnection(FtpCommandException exception)
    {
        return IsConnected && !_pendingSequence && exception.Reply.Code != 421 &&
        !exception.Reply.IsPreliminary && !exception.Reply.IsIntermediate;
    }

    /// <summary>等待会话锁。排队取消不会关闭另一项正在使用的控制连接。</summary>
    private async Task<CancellationTokenSource> AcquireAsync(CancellationToken cancellationToken, bool requireConnected = true,
        bool requireUnauthenticated = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            await _sessionGate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch
        {
            linked.Dispose();
            throw;
        }
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return requireConnected && !IsConnected
                ? throw new InvalidOperationException("请先调用 ConnectAsync 建立 FTP 会话。")
                : requireUnauthenticated && IsAuthenticated
                ? throw new InvalidOperationException("此操作必须在用户登录前执行；请先调用 ReinitializeAsync。")
                : linked;
        }
        catch
        {
            linked.Dispose();
            _ = _sessionGate.Release();
            throw;
        }
    }

    /// <summary>在会话锁下运行事务；仅明确的最终错误回复可保留会话，其他故障均关闭以避免回复错位。</summary>
    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken,
        bool requireConnected = true, bool requireUnauthenticated = false)
    {
        using CancellationTokenSource linked = await AcquireAsync(cancellationToken, requireConnected, requireUnauthenticated).ConfigureAwait(false);
        try
        {
            return await operation(linked.Token).ConfigureAwait(false);
        }
        catch (FtpCommandException exception) when (CanKeepConnection(exception))
        {
            throw;
        }
        catch (Exception exception)
        {
            Exception normalized = await HandleTransferFailureAsync(exception).ConfigureAwait(false);
            if (normalized is not FtpTransferAbortedException || !IsConnected)
                CloseConnection();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(normalized).Throw();
            throw;
        }
        finally
        {
            _ = _sessionGate.Release();
        }
    }

    /// <summary>运行无返回值的串行事务。</summary>
    private Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken,
        bool requireConnected = true, bool requireUnauthenticated = false)
    {
        return RunAsync(async token =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        }, cancellationToken,
            requireConnected, requireUnauthenticated);
    }

    /// <summary>立即关闭传输和控制连接，清除能力与所有未完成协议事务标记。</summary>
    private void CloseConnection()
    {
        Volatile.Write(ref _connected, 0);
        CloseAbortedDataChannel();
        Interlocked.Exchange(ref _controlClient, null)?.Dispose();
        Interlocked.Exchange(ref _controlStream, null)?.Dispose();
        _channel = null;
        Features = Array.Empty<string>();
        _pendingSequence = false;
        _dataChannelTls = false;
        _transferSettings = new FtpTransferSettings { DataType = FtpDataType.Ascii };
        _lastDataPreliminaryReply = null;
        ResetSessionExtensionState();
        ResetActiveTransferState();
        ResetSecurityState();
    }

    /// <summary>立即取消排队及网络操作并关闭套接字。可重复调用，也可用于中断一个尚未释放的传输流。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _lifetime.Cancel();
        CloseConnection();
        // 不释放 SemaphoreSlim：已排队的调用仍需唤醒并归还锁；它没有创建 WaitHandle，托管状态可由 GC 回收。
    }

    /// <summary>取消并释放客户端。不会等待调用者持有的传输流归还锁，因此不会发生自等待。</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}