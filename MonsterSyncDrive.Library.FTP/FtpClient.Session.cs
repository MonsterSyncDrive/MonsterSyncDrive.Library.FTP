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
using System.Globalization;

namespace MonsterSyncDrive.Library.FTP;

/// <summary>会话重置、登录前虚拟主机选择、扩展命令和机器列表选项的事务实现。</summary>
public sealed partial class FtpClient
{
    /// <summary>最近一次 OPTS MLST 实际启用的字段；HasMachineListingFactSelection 为 false 时代表尚未协商。</summary>
    public IReadOnlyList<string> SelectedMachineListingFacts { get; private set; } = Array.Empty<string>();

    /// <summary>是否已经成功协商过 OPTS MLST，包括明确要求不返回任何 facts 的情形。</summary>
    public bool HasMachineListingFactSelection
    {
        get; private set;
    }

    /// <summary>服务器已接受的 HOST 参数；没有发送 HOST 或已 REIN 时为空。</summary>
    public string? SelectedVirtualHost
    {
        get; private set;
    }

    /// <summary>
    /// 发送 REIN，保留 TCP 控制连接但清除身份、工作目录、传输参数和能力缓存。
    /// TLS 连接按 RFC 4217 在加密回复之后清除 TLS；重新登录请调用 LoginAsync。
    /// </summary>
    /// <param name="cancellationToken">取消排队或重置；无法确认协议状态时关闭连接。</param>
    public Task ReinitializeAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(async token =>
        {
            EnsureSessionTransport();
            FtpReply reply = await CommandAsync("REIN", null, token).ConfigureAwait(false);
            if (reply.Code == 120)
            {
                // 120 表示稍后就绪；后续 220 与当前 REIN 属于同一事务。
                _pendingSequence = true;
                reply = await ReadReplyAsync(token).ConfigureAwait(false);
            }
            Expect("REIN", reply, 220);
            await ResetSessionAfterReinitializeAsync(token).ConfigureAwait(false);
        }, cancellationToken, requireConnected: false);
    }

    /// <summary>
    /// 在已建立但尚未认证的控制连接上选择 FTP 虚拟主机。HOST 不是 TCP 连接地址，
    /// 必须先于 USER；已登录时不能使用此方法切换其他主机，应先 REIN。
    /// </summary>
    public Task SelectVirtualHostAsync(string host, CancellationToken cancellationToken = default)
    {
        ValidateVirtualHost(host);
        return RunAsync(async token =>
        {
            EnsureSessionTransport();
            await SelectVirtualHostCoreAsync(host, token).ConfigureAwait(false);
        }, cancellationToken, requireConnected: false, requireUnauthenticated: true);
    }

    /// <summary>通过 ALLO 为下一次传输申请空间，可选 R 参数描述最大记录大小；202 表示此服务器无需分配。</summary>
    /// <param name="bytes">请求的非负字节数。</param>
    /// <param name="maximumRecordSize">可选的最大记录大小，必须大于零。</param>
    /// <param name="cancellationToken">取消本次排队或命令。</param>
    /// <returns>200 或 202 原始回复，调用者可区分分配成功和分配不必要。</returns>
    public Task<FtpReply> AllocateAsync(long bytes, long? maximumRecordSize = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (maximumRecordSize is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRecordSize));
        string argument = bytes.ToString(CultureInfo.InvariantCulture) +
            (maximumRecordSize.HasValue ? " R " + maximumRecordSize.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);
        return RunAsync(async token =>
        {
            FtpReply reply = await CommandAsync("ALLO", argument, token).ConfigureAwait(false);
            Expect("ALLO", reply, 200, 202);
            return reply;
        }, cancellationToken);
    }

    /// <summary>通过 SMNT 请求挂载服务器定义的文件结构；不改变客户端保存的登录凭据。</summary>
    /// <returns>250 成功或 202 表示该服务器无需执行挂载。</returns>
    public Task<FtpReply> MountAsync(string path, CancellationToken cancellationToken = default)
    {
        _ = FtpPath.Validate(path);
        return RunAsync(async token =>
        {
            FtpReply reply = await CommandAsync("SMNT", path, token).ConfigureAwait(false);
            Expect("SMNT", reply, 202, 250);
            return reply;
        }, cancellationToken);
    }

    /// <summary>执行一个返回最终 2xx 回复的 SITE 操作；需要 3xx 中间状态的操作使用 ExecuteSiteSequenceAsync。</summary>
    public async Task<FtpReply> SiteAsync(string arguments, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FtpReply> replies = await ExecuteSiteSequenceAsync(
            [new FtpSiteCommand { Arguments = arguments }], cancellationToken).ConfigureAwait(false);
        return replies[0];
    }

    /// <summary>
    /// 将一组 SITE 子命令作为不可交叉的多步事务，例如 CPFR/CPTO。
    /// 必须明确声明每一步是否需要 3xx；若中间步骤失败，关闭会话以防遗留服务器状态被其他操作使用。
    /// </summary>
    public Task<IReadOnlyList<FtpReply>> ExecuteSiteSequenceAsync(IReadOnlyList<FtpSiteCommand> commands,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands);
        FtpSiteCommand[] snapshot = commands.ToArray();
        if (snapshot.Length is 0 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(commands), "SITE 事务必须包含 1 至 1024 个步骤。");
        foreach (FtpSiteCommand? command in snapshot)
        {
            ArgumentNullException.ThrowIfNull(command);
            FtpPath.ValidateArgument(command.Arguments, nameof(commands));
        }
        return snapshot[^1].RequiresContinuation
            ? throw new ArgumentException("SITE 事务最后一步必须返回最终回复。", nameof(commands))
            : RunAsync(async token =>
        {
            var replies = new List<FtpReply>(snapshot.Length);
            foreach (FtpSiteCommand? command in snapshot)
            {
                FtpReply reply = await CommandAsync("SITE", command.Arguments, token).ConfigureAwait(false);
                // 先记录中间状态，再验证调用者预期。未预期到的 1xx/3xx 也必须触发连接清理。
                if (reply.IsPreliminary || reply.IsIntermediate)
                    _pendingSequence = true;
                if (command.RequiresContinuation ? !reply.IsIntermediate : !reply.IsSuccess)
                    throw new FtpCommandException("SITE", reply);
                replies.Add(reply);
                if (!command.RequiresContinuation)
                    _pendingSequence = false;
            }
            return (IReadOnlyList<FtpReply>)replies.AsReadOnly();
        }, cancellationToken);
    }

    /// <summary>
    /// 查询服务器、文件或目录状态并返回完整多行回复。传输进行中时使用控制通道协调器，
    /// 避免等待数据流归还会话锁后才执行 STAT。
    /// </summary>
    public Task<FtpReply> GetStatusAsync(string? path = null, CancellationToken cancellationToken = default)
    {
        if (path is not null)
            _ = FtpPath.Validate(path);
        return GetValidatedStatusCoreAsync(path, cancellationToken);
    }

    /// <summary>始终通过会话/活动传输竞争协调器取得 STAT 目标，消除检查状态后数据操作刚好开始的竞态。</summary>
    private async Task<FtpReply> GetValidatedStatusCoreAsync(string? path, CancellationToken cancellationToken)
    {
        FtpReply reply = await GetTransferStatusAsync(path, cancellationToken).ConfigureAwait(false);
        Expect("STAT", reply, 211, 212, 213);
        return reply;
    }

    /// <summary>
    /// 设置服务器支持的命令选项，返回最终 2xx 回复。MLST 会同步更新字段缓存；其他服务器专用选项
    /// 由调用者按服务器文档构造，不能用此方法代替 TYPE/MODE/STRU 的专用状态设置。
    /// </summary>
    public Task<FtpReply> SetOptionsAsync(string command, string? options = null, CancellationToken cancellationToken = default)
    {
        ValidateOptionCommand(command);
        if (options is not null)
            FtpPath.ValidateArgument(options, nameof(options), allowEmpty: true);
        if (command.Equals("MLST", StringComparison.OrdinalIgnoreCase))
            ValidateFactSelection(options);
        string argument = command.ToUpperInvariant() + (options is null ? string.Empty : " " + options);
        return RunAsync(async token =>
        {
            FtpReply reply = await CommandAsync("OPTS", argument, token).ConfigureAwait(false);
            Expect("OPTS", reply, 200);
            if (command.Equals("MLST", StringComparison.OrdinalIgnoreCase))
                ApplyMachineFactSelection(reply);
            return reply;
        }, cancellationToken);
    }

    /// <summary>协商 MLST/MLSD 返回的字段。空集合表示只返回名称；保存的是服务器实际接受的子集。</summary>
    public async Task<IReadOnlyList<string>> SetMachineListingFactsAsync(IEnumerable<string> factNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factNames);
        string[] names = factNames.ToArray();
        foreach (string? name in names)
            ValidateFactName(name, nameof(factNames));
        string selection = string.Join(';', names.Distinct(StringComparer.OrdinalIgnoreCase));
        if (selection.Length > 0)
            selection += ";";
        FtpReply reply = await SetOptionsAsync("MLST", selection, cancellationToken).ConfigureAwait(false);
        // 返回当前请求的确认结果，而不是再次读取共享状态；另一个已排队的 OPTS 可能已先更新缓存。
        return Array.AsReadOnly(ParseMachineFactSelection(reply));
    }

    /// <summary>
    /// 使用 MFCT 设置 UTC 创建时间，并返回服务器实际保存的时间（文件系统可能降低小数秒精度）。
    /// 此扩展需要服务器支持；服务器错误保持为 FtpCommandException。
    /// </summary>
    public Task<DateTimeOffset> SetCreatedTimeAsync(string path, DateTimeOffset createdTime, CancellationToken cancellationToken = default)
    {
        _ = FtpPath.Validate(path);
        string time = createdTime.UtcDateTime.ToString("yyyyMMddHHmmss.fffffff", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return RunAsync(async token =>
        {
            FtpReply reply = await CommandAsync("MFCT", time + " " + path, token).ConfigureAwait(false);
            Expect("MFCT", reply, 213);
            string message = reply.Message;
            int end = message.IndexOf(';');
            return !message.StartsWith("Create=", StringComparison.OrdinalIgnoreCase) || end <= 7 ||
                end + 1 >= message.Length || message[end + 1] != ' '
                ? throw new FtpProtocolException("MFCT 成功回复没有包含实际的 Create 时间。")
                : FtpListingParser.ParseTimestamp(message[7..end]);
        }, cancellationToken);
    }

    /// <summary>向未认证会话发送 HOST，供初次连接和 REIN 后重新登录的共同路径使用。</summary>
    private async Task SelectVirtualHostCoreAsync(string host, CancellationToken cancellationToken)
    {
        ValidateVirtualHost(host);
        Expect("HOST", await CommandAsync("HOST", host, cancellationToken).ConfigureAwait(false), 220);
        SelectedVirtualHost = host;
    }

    /// <summary>清除由 REIN、断线和重新连接重置的扩展会话状态。</summary>
    private void ResetSessionExtensionState()
    {
        SelectedMachineListingFacts = Array.Empty<string>();
        HasMachineListingFactSelection = false;
        SelectedVirtualHost = null;
    }

    /// <summary>验证有可用控制连接；仅有配置的客户端不能发送登录前命令。</summary>
    private void EnsureSessionTransport()
    {
        if (!IsTransportConnected)
            throw new InvalidOperationException("请先建立 FTP 控制连接。");
    }

    /// <summary>验证 HOST 是单个主机名/IP，不允许附带协议、端口或路径。</summary>
    private static void ValidateVirtualHost(string host)
    {
        FtpPath.ValidateArgument(host, nameof(host));
        bool bracketed = host.StartsWith('[') || host.EndsWith(']');
        bool valid = bracketed
            ? host.StartsWith('[') && host.EndsWith(']') && Uri.CheckHostName(host[1..^1]) == UriHostNameType.IPv6
            : Uri.CheckHostName(host) != UriHostNameType.Unknown;
        if (!valid || host.Contains('/') || host.Contains('\\'))
            throw new ArgumentException("HOST 参数必须为主机名或 IP 地址，不包含端口和路径。", nameof(host));
    }

    /// <summary>验证 OPTS 后的命令标签，保留扩展命令可能使用的数字和短横线。</summary>
    private static void ValidateOptionCommand(string command)
    {
        FtpPath.ValidateArgument(command, nameof(command));
        if (command.Length > 32 || command.Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("OPTS 命令标签只能包含 ASCII 字母、数字、短横线或下划线。", nameof(command));
    }

    /// <summary>验证一个可安全用于分号分隔列表的 MLST fact 名称。</summary>
    private static void ValidateFactName(string name, string parameter)
    {
        ArgumentNullException.ThrowIfNull(name, parameter);
        if (name.Length == 0 || name.Length > 128 || name.Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
            throw new ArgumentException("MLST fact 名称只能包含 ASCII 字母、数字、点、短横线或下划线。", parameter);
    }

    /// <summary>验证通用 OPTS MLST 参数，避免接受后才发现本地无法解析选项。</summary>
    private static void ValidateFactSelection(string? selection)
    {
        if (string.IsNullOrEmpty(selection))
            return;
        if (selection[^1] != ';')
            throw new ArgumentException("MLST fact 列表必须以分号结束。", nameof(selection));
        foreach (string name in selection[..^1].Split(';'))
            ValidateFactName(name, nameof(selection));
    }

    /// <summary>解析 RFC 3659 的 MLST OPTS 回复，并同步 FEAT 中表示当前选择的星号。</summary>
    private void ApplyMachineFactSelection(FtpReply reply)
    {
        string[] selected = ParseMachineFactSelection(reply);
        SelectedMachineListingFacts = Array.AsReadOnly(selected);
        HasMachineListingFactSelection = true;

        var featureLines = Features.ToList();
        var index = featureLines.FindIndex(static line => line.Equals("MLST", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("MLST ", StringComparison.OrdinalIgnoreCase));
        List<string> advertised = index < 0 || featureLines[index].Length <= 5 ? [] :
            featureLines[index][5..].Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(static value => value.TrimEnd('*')).ToList();
        foreach (string name in selected)
            if (!advertised.Contains(name, StringComparer.OrdinalIgnoreCase))
                advertised.Add(name);
        string line = advertised.Count == 0 ? "MLST" : "MLST " + string.Join(';', advertised.Select(name =>
            name + (selected.Contains(name, StringComparer.OrdinalIgnoreCase) ? "*" : string.Empty))) + ";";
        if (index >= 0)
            featureLines[index] = line;
        else
            featureLines.Add(line);
        Features = new ReadOnlyCollection<string>(featureLines);
    }

    /// <summary>读取服务器实际接受的字段集合；调用者可独立持有结果而不受后续并发选项修改影响。</summary>
    private static string[] ParseMachineFactSelection(FtpReply reply)
    {
        const string prefix = "MLST OPTS";
        string message = reply.Message;
        if (!message.Equals(prefix, StringComparison.OrdinalIgnoreCase) &&
            !message.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
            throw new FtpProtocolException("OPTS MLST 成功回复没有包含实际选择的字段。");
        string selection = message.Length > prefix.Length ? message[(prefix.Length + 1)..] : string.Empty;
        try
        {
            ValidateFactSelection(selection);
        }
        catch (ArgumentException)
        {
            throw new FtpProtocolException("OPTS MLST 返回了无效的字段列表。");
        }
        return selection.Length == 0 ? Array.Empty<string>() :
            selection[..^1].Split(';').Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
