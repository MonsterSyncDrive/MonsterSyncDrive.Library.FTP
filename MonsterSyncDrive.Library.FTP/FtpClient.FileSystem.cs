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
using System.Text;

namespace MonsterSyncDrive.FTP;

/// <summary>远程工作目录、文件元数据、目录修改及可选服务器扩展。</summary>
public sealed partial class FtpClient
{
    /// <summary>读取 PWD，并正确解码 RFC 959 中双写的引号。</summary>
    public Task<string> GetWorkingDirectoryAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            var reply = await CommandAsync("PWD", null, token).ConfigureAwait(false);
            Expect("PWD", reply, 257);
            var text = reply.Message;
            if (text.Length == 0 || text[0] != '"') throw new FtpProtocolException("PWD 未返回带引号的目录。");
            var path = new StringBuilder();
            for (var index = 1; index < text.Length; index++)
            {
                if (text[index] != '"') { path.Append(text[index]); continue; }
                if (index + 1 < text.Length && text[index + 1] == '"') { path.Append('"'); index++; continue; }
                return path.ToString();
            }
            throw new FtpProtocolException("PWD 返回了未闭合的目录引号。");
        }, cancellationToken);

    /// <summary>改变服务器工作目录；相对路径始终相对于服务器当前工作目录。</summary>
    public Task SetWorkingDirectoryAsync(string path, CancellationToken cancellationToken = default) => PathCommandAsync("CWD", path, [250], cancellationToken);

    /// <summary>进入父目录。</summary>
    public Task ChangeToParentDirectoryAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async token => { Expect("CDUP", await CommandAsync("CDUP", null, token).ConfigureAwait(false), 200, 250); }, cancellationToken);

    /// <summary>查询二进制模式下的文件大小。权限错误和不存在均保留服务器异常，不猜测零字节。</summary>
    public Task<long> GetFileSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        FtpPath.Validate(path);
        return RunAsync(token => GetSizeCoreAsync(path, token), cancellationToken);
    }

    /// <summary>读取 RFC 3659 MDTM UTC 修改时间，包括小数秒。</summary>
    public Task<DateTimeOffset> GetModifiedTimeAsync(string path, CancellationToken cancellationToken = default)
    {
        FtpPath.Validate(path);
        return RunAsync(async token =>
        {
            var reply = await CommandAsync("MDTM", path, token).ConfigureAwait(false);
            Expect("MDTM", reply, 213);
            return FtpListingParser.ParseTimestamp(reply.Message.Trim());
        }, cancellationToken);
    }

    /// <summary>使用服务器可选 MFMT 扩展设置 UTC 修改时间；不支持时保留错误回复。</summary>
    public Task SetModifiedTimeAsync(string path, DateTimeOffset modifiedTime, CancellationToken cancellationToken = default)
    {
        FtpPath.Validate(path);
        return RunAsync(async token =>
        {
            var argument = modifiedTime.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " " + path;
            Expect("MFMT", await CommandAsync("MFMT", argument, token).ConfigureAwait(false), 200, 213, 250);
        }, cancellationToken);
    }

    /// <summary>创建目录；recursive 为 true 时逐层创建，并验证已存在的条目确为目录。</summary>
    public Task CreateDirectoryAsync(string path, bool recursive = false, CancellationToken cancellationToken = default)
    {
        FtpPath.Validate(path);
        if (!recursive) return PathCommandAsync("MKD", path, [257, 250], cancellationToken);
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(static part => part is "." or "..") || path.Contains('\\'))
            throw new ArgumentException("递归创建路径不可包含点目录或反斜杠。", nameof(path));
        return RunAsync(async token =>
        {
            var current = path.StartsWith('/') ? "/" : string.Empty;
            foreach (var part in parts)
            {
                current = current.Length == 0 ? part : FtpPath.Combine(current, part);
                var reply = await CommandAsync("MKD", current, token).ConfigureAwait(false);
                if (reply.Code is 257 or 250) continue;
                // 550 既可能是已存在，也可能是权限不足。必须再次确认类型，不能静默吞掉创建失败。
                if (reply.Code == 550 && (await GetItemCoreAsync(current, token).ConfigureAwait(false))?.Type == FtpItemType.Directory) continue;
                throw new FtpCommandException("MKD", reply);
            }
        }, cancellationToken);
    }

    /// <summary>删除单个文件或符号链接；不会跟随链接。</summary>
    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default) =>
        PathCommandAsync("DELE", path, [250], cancellationToken);

    /// <summary>
    /// 删除目录。递归时先枚举并验证整个删除计划，再按子项优先的顺序删除；不跟随符号链接。
    /// FTP 不提供事务，删除过程中服务器出错或目录被其他会话修改时可能已删除部分条目。
    /// </summary>
    public Task DeleteDirectoryAsync(string path, bool recursive = false, CancellationToken cancellationToken = default)
    {
        FtpPath.Validate(path);
        if (!recursive) return PathCommandAsync("RMD", path, [250], cancellationToken);
        FtpPath.ValidateRecursiveTarget(path);
        return RunAsync(async token =>
        {
            var files = new List<FtpItem>();
            var directories = new List<string>();
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((path, 0));
            while (pending.TryPop(out var current))
            {
                if (current.Depth >= _options.MaxRecursionDepth) throw new FtpProtocolException("删除计划超过递归深度限制。");
                directories.Add(current.Path);
                foreach (var item in await ListCoreAsync(current.Path, FtpListingMode.Auto, token).ConfigureAwait(false))
                {
                    if (files.Count + directories.Count + pending.Count >= _options.MaxRecursiveEntries)
                        throw new FtpProtocolException("删除计划超过总条目数限制。");
                    if (item.Type == FtpItemType.Directory) pending.Push((item.FullPath, current.Depth + 1));
                    else if (item.Type is FtpItemType.File or FtpItemType.SymbolicLink) files.Add(item);
                    else throw new FtpProtocolException("删除计划包含未知类型；停止以防误删。");
                }
            }
            foreach (var file in files) Expect("DELE", await CommandAsync("DELE", file.FullPath, token).ConfigureAwait(false), 250);
            // 先序遍历得到父目录在前，逆序即可保证所有后代都早于父目录删除。
            for (var index = directories.Count - 1; index >= 0; index--)
                Expect("RMD", await CommandAsync("RMD", directories[index], token).ConfigureAwait(false), 250);
        }, cancellationToken);
    }

    /// <summary>用连续的 RNFR/RNTO 命令重命名或移动条目；是否覆盖目标由服务器策略决定。</summary>
    public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        FtpPath.Validate(sourcePath);
        FtpPath.Validate(destinationPath);
        return RunAsync(async token =>
        {
            Expect("RNFR", await CommandAsync("RNFR", sourcePath, token).ConfigureAwait(false), 350);
            _pendingSequence = true;
            Expect("RNTO", await CommandAsync("RNTO", destinationPath, token).ConfigureAwait(false), 250);
            _pendingSequence = false;
        }, cancellationToken);
    }

    /// <summary>通过可选 SITE CHMOD 设置三位或四位八进制权限，例如 755 或 0644。</summary>
    public Task SetPermissionsAsync(string path, string permissions, CancellationToken cancellationToken = default)
    {
        FtpPath.Validate(path);
        ArgumentNullException.ThrowIfNull(permissions);
        if (permissions.Length is < 3 or > 4 || permissions.Any(static c => c is < '0' or > '7'))
            throw new ArgumentException("权限必须为三位或四位八进制字符串。", nameof(permissions));
        return RunAsync(async token =>
        {
            Expect("SITE", await CommandAsync("SITE", "CHMOD " + permissions + " " + path, token).ConfigureAwait(false), 200, 250);
        }, cancellationToken);
    }

    /// <summary>查询服务器摘要。优先 HASH/OPTS HASH；未公布 HASH 时使用 XSHA256/XSHA512/XSHA1/XMD5 扩展。</summary>
    public Task<FtpChecksum> GetChecksumAsync(string path, FtpHashAlgorithm algorithm = FtpHashAlgorithm.Sha256,
        CancellationToken cancellationToken = default)
    {
        FtpPath.Validate(path);
        if (!Enum.IsDefined(algorithm)) throw new ArgumentOutOfRangeException(nameof(algorithm));
        return RunAsync(async token =>
        {
            var (name, extension, length) = algorithm switch
            {
                FtpHashAlgorithm.Sha256 => ("SHA-256", "XSHA256", 64),
                FtpHashAlgorithm.Sha512 => ("SHA-512", "XSHA512", 128),
                FtpHashAlgorithm.Sha1 => ("SHA-1", "XSHA1", 40),
                _ => ("MD5", "XMD5", 32)
            };
            var verb = extension;
            if (SupportsFeature("HASH"))
            {
                Expect("OPTS", await CommandAsync("OPTS", "HASH " + name, token).ConfigureAwait(false), 200);
                verb = "HASH";
            }
            var reply = await CommandAsync(verb, path, token).ConfigureAwait(false);
            Expect(verb, reply, 213, 250);
            var hash = reply.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(value => value.Length == length && value.All(char.IsAsciiHexDigit));
            if (hash is null) throw new FtpProtocolException("服务器未返回与所选算法匹配的十六进制摘要。");
            return new FtpChecksum { Algorithm = algorithm, Value = hash.ToLowerInvariant() };
        }, cancellationToken);
    }

    /// <summary>在同一会话事务中查询并严格解析非负 SIZE 值。</summary>
    private async Task<long> GetSizeCoreAsync(string path, CancellationToken cancellationToken)
    {
        var reply = await CommandAsync("SIZE", path, cancellationToken).ConfigureAwait(false);
        Expect("SIZE", reply, 213);
        if (!long.TryParse(reply.Message.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            throw new FtpProtocolException("SIZE 回复不是有效的非负 64 位整数。");
        return size;
    }

    /// <summary>验证并执行单步路径命令，保持错误回复的可诊断性。</summary>
    private Task PathCommandAsync(string verb, string path, int[] codes, CancellationToken cancellationToken)
    {
        FtpPath.Validate(path);
        return RunAsync(async token => { Expect(verb, await CommandAsync(verb, path, token).ConfigureAwait(false), codes); }, cancellationToken);
    }
}