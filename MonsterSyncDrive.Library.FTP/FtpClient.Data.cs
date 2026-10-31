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
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace MonsterSyncDrive.Library.FTP;

/// <summary>FTP 数据端点协商、连接认证与完成回复处理。</summary>
public sealed partial class FtpClient
{
    /// <summary>应用端数据编解码流；与实际 TCP/TLS 流分开，确保先结束压缩/分帧再发送 TLS EOF。</summary>
    private Stream? _dataApplicationStream;

    /// <summary>为一条数据命令建立通道；REST 与传输命令连续发送，不允许其他命令消耗断点标记。</summary>
    private async Task<Stream> StartDataAsync(string verb, string? path, long restartOffset, CancellationToken cancellationToken,
        string? restartMarker = null, bool rawData = false)
    {
        ValidateByteRestart(restartOffset);
        if (restartMarker is not null)
            ValidateOpaqueRestartMarker(restartMarker);
        if (!rawData && _transferSettings.Structure == FtpFileStructure.Page && verb is "RETR" or "STOR" or "APPE" or "STOU")
            throw new InvalidOperationException("页结构必须使用 DownloadPagesAsync/UploadPagesAsync，以保留逻辑页头。");
        FtpActiveTransfer active = BeginActiveTransfer(cancellationToken);
        _restartCheckpoints.Clear();
        TcpListener? listener = null;
        try
        {
            if (_options.DataConnectionMode is FtpDataConnectionMode.AutoPassive or FtpDataConnectionMode.ExtendedPassive or
                FtpDataConnectionMode.Passive or FtpDataConnectionMode.LongPassive)
                await OpenPassiveAsync(cancellationToken).ConfigureAwait(false);
            else
                listener = await OpenActiveAsync(cancellationToken).ConfigureAwait(false);

            if (restartOffset > 0 || restartMarker is not null)
            {
                Expect("REST", await CommandAsync("REST", restartMarker ?? restartOffset.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false), 350);
                _pendingSequence = true;
            }
            FtpReply preliminary = await CommandAsync(verb, path, cancellationToken).ConfigureAwait(false);
            Expect(verb, preliminary, 125, 150);
            _lastDataPreliminaryReply = preliminary;
            _pendingSequence = true;
            active.MarkReady();
            CancellationToken dataToken = GetActiveTransferToken(cancellationToken);
            if (listener is not null)
            {
                _dataClient = await FtpTimeout.RunAsync(async token =>
                {
                    // 主动模式只接受控制通道对端。其他主机连入时立即丢弃，并在同一总截止时间内继续等待。
                    while (true)
                    {
                        TcpClient candidate = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                        if (SameAddress(((IPEndPoint)candidate.Client.RemoteEndPoint!).Address, ControlPeer().Address))
                            return candidate;
                        candidate.Dispose();
                    }
                }, _options.DataTimeout, dataToken, "主动数据连接").ConfigureAwait(false);
            }
            _dataClient!.NoDelay = true;
            _dataStream = _dataClient.GetStream();
            if (_dataChannelTls)
                _dataStream = await AuthenticateTlsStreamAsync(_dataStream, true, dataToken).ConfigureAwait(false);
            FtpTimedDataStream timed = new(_dataStream, _options.DataTimeout, dataToken);
            if (rawData)
                return timed;
            // LIST/NLST/MLSx 的文本表示不受文件 TYPE/STRU 的设置影响，但仍遵循协商的传输 MODE。
            FtpTransferSettings settings = verb is "LIST" or "NLST" or "MLSD"
                ? _transferSettings with
                {
                    DataType = FtpDataType.Ascii,
                    PreserveRepresentation = true,
                    Structure = FtpFileStructure.File
                }
                : _transferSettings;
            _dataApplicationStream = verb is "RETR" or "LIST" or "NLST" or "MLSD"
                ? FtpDataCodec.CreateReadStream(timed, settings, leaveOpen: true)
                : FtpDataCodec.CreateWriteStream(timed, settings, leaveOpen: true);
            return _dataApplicationStream;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _dataClient, null)?.Dispose();
            Interlocked.Exchange(ref _dataStream, null)?.Dispose();
            EndActiveTransferPreparation();
            Exception normalized = await HandleTransferFailureAsync(exception).ConfigureAwait(false);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(normalized).Throw();
            throw;
        }
        finally
        {
            listener?.Stop();
        }
    }

    /// <summary>执行 EPSV/PASV 协商并连接数据端点；仅明确不支持 EPSV 且使用 IPv4 时回退。</summary>
    private async Task OpenPassiveAsync(CancellationToken cancellationToken)
    {
        IPEndPoint peer = ControlPeer();
        var usePasv = _options.DataConnectionMode == FtpDataConnectionMode.Passive;
        var port = 0;
        IPAddress address = peer.Address;
        if (_options.DataConnectionMode == FtpDataConnectionMode.LongPassive)
        {
            FtpReply lpsv = await CommandAsync("LPSV", null, cancellationToken).ConfigureAwait(false);
            Expect("LPSV", lpsv, 228);
            IPEndPoint endpoint = FtpLongEndpointCodec.ParsePassive(lpsv.Message);
            port = endpoint.Port;
            if (_options.TrustPassiveAddress)
                address = endpoint.Address;
        }
        else if (!usePasv)
        {
            FtpReply epsv = await CommandAsync("EPSV", null, cancellationToken).ConfigureAwait(false);
            if (epsv.Code == 229)
                port = ParseExtendedPort(epsv.Message);
            else
                usePasv = _options.DataConnectionMode == FtpDataConnectionMode.AutoPassive &&
                     peer.AddressFamily == AddressFamily.InterNetwork && IsUnsupported(epsv)
                ? true
                : throw new FtpCommandException("EPSV", epsv);
        }
        if (usePasv)
        {
            if (peer.AddressFamily != AddressFamily.InterNetwork)
                throw new NotSupportedException("IPv6 连接必须使用 EPSV。");
            FtpReply pasv = await CommandAsync("PASV", null, cancellationToken).ConfigureAwait(false);
            Expect("PASV", pasv, 227);
            IPEndPoint endpoint = ParsePassiveEndpoint(pasv.Message);
            port = endpoint.Port;
            if (_options.TrustPassiveAddress)
                address = endpoint.Address;
        }
        TcpClient client = new(address.AddressFamily);
        _dataClient = client;
        await FtpTimeout.RunAsync(token => client.ConnectAsync(address, port, token).AsTask(),
            _options.ConnectTimeout, cancellationToken, "被动数据连接").ConfigureAwait(false);
    }

    /// <summary>监听本地数据端口并公布 EPRT/PORT。公布地址与绑定地址可分离，以支持显式 NAT 映射。</summary>
    private async Task<TcpListener> OpenActiveAsync(CancellationToken cancellationToken)
    {
        IPEndPoint local = NormalizeEndpoint((IPEndPoint)_controlClient!.Client.LocalEndPoint!);
        IPAddress bind = _options.ActiveBindAddress ?? local.Address;
        IPAddress advertised = _options.ActiveAdvertisedAddress ?? bind;
        if (bind.AddressFamily != local.AddressFamily || advertised.AddressFamily != local.AddressFamily)
            throw new ArgumentException("主动模式地址必须与控制连接使用相同地址族。");
        TcpListener listener = new(bind, _options.ActivePort);
        try
        {
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (_options.DataConnectionMode == FtpDataConnectionMode.LongActive)
            {
                Expect("LPRT", await CommandAsync("LPRT", FtpLongEndpointCodec.FormatActive(new IPEndPoint(advertised, port)),
                    cancellationToken).ConfigureAwait(false), 200);
                return listener;
            }
            var usePort = _options.DataConnectionMode == FtpDataConnectionMode.Active;
            if (!usePort)
            {
                var family = advertised.AddressFamily == AddressFamily.InterNetwork ? "1" : "2";
                FtpReply eprt = await CommandAsync("EPRT", $"|{family}|{advertised}|{port.ToString(CultureInfo.InvariantCulture)}|",
                    cancellationToken).ConfigureAwait(false);
                if (eprt.Code == 200)
                    return listener;
                usePort = _options.DataConnectionMode == FtpDataConnectionMode.AutoActive &&
                    local.AddressFamily == AddressFamily.InterNetwork && IsUnsupported(eprt)
                    ? true
                    : throw new FtpCommandException("EPRT", eprt);
            }
            if (usePort)
            {
                if (advertised.AddressFamily != AddressFamily.InterNetwork)
                    throw new NotSupportedException("IPv6 连接必须使用 EPRT。");
                IEnumerable<string> octets = advertised.GetAddressBytes().Select(static octet => octet.ToString(CultureInfo.InvariantCulture));
                Expect("PORT", await CommandAsync("PORT",
                    string.Join(',', octets) + "," + (port / 256).ToString(CultureInfo.InvariantCulture) + "," +
                    (port % 256).ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false), 200);
            }
            return listener;
        }
        catch
        {
            listener.Stop();
            throw;
        }
    }

    /// <summary>关闭数据流后读取最终完成回复。发送完字节或收到 EOF 本身并不代表传输成功。</summary>
    private async Task<FtpReply> CompleteDataAsync(CancellationToken cancellationToken)
    {
        Stream? application = Interlocked.Exchange(ref _dataApplicationStream, null);
        Stream? stream = Interlocked.Exchange(ref _dataStream, null);
        var applicationDisposed = false;
        try
        {
            if (application is FtpDataWriteStream encoded)
                await FtpTimeout.RunAsync(encoded.CompleteAsync, _options.DataTimeout,
                    GetActiveTransferToken(cancellationToken), "传输分帧结束").ConfigureAwait(false);
            if (application is not null)
                await application.DisposeAsync().ConfigureAwait(false);
            applicationDisposed = true;
            FtpTlsSession? tlsSession = _dataTlsSession;
            if (tlsSession is not null)
            {
                // TLS 上传必须先发送 close_notify，单纯释放 TCP 会被服务器当作截断数据，
                // 即便文件字节已经全部发出也可能没有 226。Shutdown 只关闭 TLS 写方向，不等待对端。
                CancellationToken dataToken = GetActiveTransferToken(cancellationToken);
                await FtpTimeout.RunAsync(async token =>
                {
                    await tlsSession.ShutdownAsync(token).ConfigureAwait(false);
                    // 必须消费对端关闭通知及可能在途的 TLS session ticket，再关闭 TCP。
                    // 若在仍有未读 TLS 记录时直接 Dispose 套接字，部分平台会发 RST，服务器可能丢掉上传尾部或 226。
                    var one = new byte[1];
                    if (await tlsSession.Stream.ReadAsync(one, token).ConfigureAwait(false) != 0)
                        throw new FtpProtocolException("TLS 数据结束后出现了未消费的应用数据。");
                }, _options.DataTimeout, dataToken, "TLS 数据结束").ConfigureAwait(false);
            }
        }
        finally
        {
            if (!applicationDisposed && application is not null)
            {
                // 完成分帧失败也必须释放 zlib；先关闭网络，避免清理误写成功尾帧。
                Interlocked.Exchange(ref _dataClient, null)?.Dispose();
                if (application is FtpDataWriteStream writer)
                    writer.Abort();
                try
                {
                    await application.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            }
            // 即使 TLS 结束通知超时，也必须释放已经从会话字段移出的流和原生 TLS 资源。
            try
            {
                if (stream is not null)
                    await stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _dataClient, null)?.Dispose();
                Interlocked.Exchange(ref _dataTlsSession, null)?.Dispose();
            }
        }
        return await ReadTransferCompletionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>取得控制连接实际对端；数据连接不重新解析 DNS，避免多地址服务器造成会话错配。</summary>
    private IPEndPoint ControlPeer()
    {
        return NormalizeEndpoint((IPEndPoint)(_controlClient?.Client.RemoteEndPoint
        ?? throw new ObjectDisposedException(nameof(FtpClient), "控制连接已关闭。")));
    }

    /// <summary>双栈控制套接字可能把 IPv4 表示成映射 IPv6，数据协商必须使用实际的 IPv4 地址族。</summary>
    private static IPEndPoint NormalizeEndpoint(IPEndPoint endpoint)
    {
        return endpoint.Address.IsIPv4MappedToIPv6
        ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port) : endpoint;
    }

    /// <summary>统一 IPv4 与 IPv4 映射 IPv6 地址比较。</summary>
    private static bool SameAddress(IPAddress left, IPAddress right)
    {
        return left.MapToIPv6().Equals(right.MapToIPv6());
    }

    /// <summary>严格解析 RFC 2428 的任意可打印分隔符与非零端口，不假定一定使用竖线。</summary>
    private static int ParseExtendedPort(string message)
    {
        var start = message.LastIndexOf('(');
        var end = message.IndexOf(')', Math.Max(0, start));
        if (start < 0 || end <= start + 5)
            throw new FtpProtocolException("EPSV 回复缺少有效端点。");
        var value = message[(start + 1)..end];
        var delimiter = value[0];
        return delimiter is < '!' or > '~' || value[1] != delimiter || value[2] != delimiter || value[^1] != delimiter ||
            !int.TryParse(value.AsSpan(3, value.Length - 4), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535
            ? throw new FtpProtocolException("EPSV 回复的分隔符或端口无效。")
            : port;
    }

    /// <summary>严格解析 PASV 的六个八位整数，拒绝零端口与越界整数。</summary>
    private static IPEndPoint ParsePassiveEndpoint(string message)
    {
        var start = message.LastIndexOf('(');
        var end = message.IndexOf(')', Math.Max(0, start));
        if (start < 0 || end <= start)
            throw new FtpProtocolException("PASV 回复缺少端点。");
        var parts = message[(start + 1)..end].Split(',');
        if (parts.Length != 6)
            throw new FtpProtocolException("PASV 回复必须包含六个整数。");
        var values = new byte[6];
        for (var index = 0; index < values.Length; index++)
        {
            if (!byte.TryParse(parts[index].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out values[index]))
                throw new FtpProtocolException("PASV 回复含越界整数。");
        }
        var port = (values[4] * 256) + values[5];
        return port == 0 ? throw new FtpProtocolException("PASV 回复含零端口。") : new IPEndPoint(new IPAddress(values[..4]), port);
    }
}