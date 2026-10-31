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

namespace MonsterSyncDrive.Library.FTP;

/// <summary>目录枚举、MLST 元数据和受限递归操作。</summary>
public sealed partial class FtpClient
{
    /// <summary>枚举一层目录；优先 MLSD，旧服务器可回退明确可识别的 UNIX/DOS LIST。</summary>
    /// <param name="directory">远程目录；默认当前目录。路径原样发送。</param>
    /// <param name="mode">机器列表、传统列表或自动回退。</param>
    /// <param name="cancellationToken">取消排队或网络操作。</param>
    public Task<IReadOnlyList<FtpItem>> ListAsync(string directory = ".", FtpListingMode mode = FtpListingMode.Auto,
        CancellationToken cancellationToken = default)
    {
        _ = FtpPath.Validate(directory);
        return !Enum.IsDefined(mode)
            ? throw new ArgumentOutOfRangeException(nameof(mode))
            : RunAsync(token => ListCoreAsync(directory, mode, token), cancellationToken);
    }

    /// <summary>通过 NLST 获取服务器原样返回的名称；不同服务器可能返回名称或带目录前缀的路径。</summary>
    public Task<IReadOnlyList<string>> GetNameListingAsync(string directory = ".", CancellationToken cancellationToken = default)
    {
        _ = FtpPath.Validate(directory);
        return RunAsync(async token => (await ReadListingAsync("NLST", directory, token).ConfigureAwait(false)).Lines, cancellationToken);
    }

    /// <summary>获取原始 LIST 文本，适用于非 UNIX/DOS 的服务器专用列表格式。</summary>
    public Task<string> GetRawListingAsync(string directory = ".", CancellationToken cancellationToken = default)
    {
        _ = FtpPath.Validate(directory);
        return RunAsync(async token => (await ReadListingAsync("LIST", directory, token).ConfigureAwait(false)).Text, cancellationToken);
    }

    /// <summary>递归枚举目录，不跟随符号链接；总条目数和深度受配置限制。遇到未知类型时停止并报错。</summary>
    public Task<IReadOnlyList<FtpItem>> ListRecursiveAsync(string directory, CancellationToken cancellationToken = default)
    {
        _ = FtpPath.Validate(directory);
        return RunAsync(async token =>
        {
            List<FtpItem> results = [];
            Stack<(string Path, int Depth)> pending = new();
            pending.Push((directory, 0));
            while (pending.TryPop(out (string Path, int Depth) current))
            {
                token.ThrowIfCancellationRequested();
                if (current.Depth >= _options.MaxRecursionDepth)
                    throw new FtpProtocolException("目录树超过配置的递归深度。");
                IReadOnlyList<FtpItem> entries = await ListCoreAsync(current.Path, FtpListingMode.Auto, token).ConfigureAwait(false);
                foreach (FtpItem item in entries)
                {
                    if (results.Count >= _options.MaxRecursiveEntries)
                        throw new FtpProtocolException("目录树超过配置的总条目数。");
                    if (item.Type == FtpItemType.Unknown)
                        throw new FtpProtocolException("无法安全递归未知类型的目录条目。");
                    results.Add(item);
                    if (item.Type == FtpItemType.Directory)
                        pending.Push((item.FullPath, current.Depth + 1));
                }
            }
            return (IReadOnlyList<FtpItem>)results.AsReadOnly();
        }, cancellationToken);
    }

    /// <summary>
    /// 获取单个条目。优先 MLST，不支持时列举父目录；550 表示不可用，返回空（可能不存在或无权限）。
    /// FTP 的 550 无法可靠区分不存在和无权限，调用者不能用空值证明资源不存在。
    /// </summary>
    public Task<FtpItem?> GetItemAsync(string path, CancellationToken cancellationToken = default)
    {
        _ = FtpPath.Validate(path);
        return RunAsync(token => GetItemCoreAsync(path, token), cancellationToken);
    }

    /// <summary>条目是否可见且为普通文件。550 返回 false，也可能表示缺少访问权限。</summary>
    public async Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        return (await GetItemAsync(path, cancellationToken).ConfigureAwait(false))?.Type == FtpItemType.File;
    }

    /// <summary>条目是否可见且为目录。550 返回 false，也可能表示缺少访问权限。</summary>
    public async Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        return (await GetItemAsync(path, cancellationToken).ConfigureAwait(false))?.Type == FtpItemType.Directory;
    }

    /// <summary>在当前事务中获取一个条目，不额外申请会话锁。</summary>
    private async Task<FtpItem?> GetItemCoreAsync(string path, CancellationToken cancellationToken)
    {
        FtpReply reply = await CommandAsync("MLST", path, cancellationToken).ConfigureAwait(false);
        if (reply.Code == 550)
            return null;
        if (reply.Code == 250)
        {
            foreach (var line in reply.Lines.Skip(1).SkipLast(1))
            {
                if (line.StartsWith(' '))
                {
                    FtpItem? item = FtpListingParser.ParseMachine(line, path, singleItem: true);
                    if (item is not null)
                        return item;
                }
            }
            throw new FtpProtocolException("MLST 成功回复未包含条目 facts。");
        }
        if (!IsUnsupported(reply))
            throw new FtpCommandException("MLST", reply);
        var directory = FtpPath.GetDirectoryName(path);
        var name = FtpPath.GetFileName(path);
        return (await ListCoreAsync(directory, FtpListingMode.Auto, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Name.Equals(name, StringComparison.Ordinal));
    }

    /// <summary>读取并解析一层列表；仅 MLSD 命令自身明确不支持时才允许回退。</summary>
    private async Task<IReadOnlyList<FtpItem>> ListCoreAsync(string directory, FtpListingMode mode, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> lines;
        var machine = mode != FtpListingMode.Legacy;
        try
        {
            lines = (await ReadListingAsync(machine ? "MLSD" : "LIST", directory, cancellationToken).ConfigureAwait(false)).Lines;
        }
        catch (FtpCommandException exception) when (mode == FtpListingMode.Auto && exception.Command == "MLSD" &&
            IsUnsupported(exception.Reply) && !_pendingSequence)
        {
            machine = false;
            lines = (await ReadListingAsync("LIST", directory, cancellationToken).ConfigureAwait(false)).Lines;
        }
        if (!machine)
            return FtpLegacyListingParser.ParseLines(lines, directory, _options.TimeProvider);
        List<FtpItem> entries = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            FtpItem? item = FtpListingParser.ParseMachine(line, directory);
            if (item is null || item.Name is "." or "..")
                continue;
            if (!names.Add(item.Name))
                throw new FtpProtocolException("MLSD 包含重复的子项名称。");
            entries.Add(item);
        }
        return entries.AsReadOnly();
    }

    /// <summary>通过数据连接读取有界文本。先检查大小再增长缓冲，且必须消费最终 226/250 回复。</summary>
    private async Task<(string Text, IReadOnlyList<string> Lines)> ReadListingAsync(string verb, string directory,
        CancellationToken cancellationToken)
    {
        Stream stream = await StartDataAsync(verb, directory, 0, cancellationToken).ConfigureAwait(false);
        CancellationToken dataToken = GetActiveTransferToken(cancellationToken);
        using MemoryStream content = new();
        var buffer = new byte[_options.BufferSize];
        while (true)
        {
            var read = await FtpTimeout.RunAsync(token => stream.ReadAsync(buffer, token).AsTask(),
                _options.DataTimeout, dataToken, "目录数据读取").ConfigureAwait(false);
            if (read == 0)
                break;
            if (content.Length + read > _options.MaxListingBytes)
                throw new FtpProtocolException("目录列表超过配置的字节限制。");
            content.Write(buffer, 0, read);
        }
        _ = await CompleteDataAsync(cancellationToken).ConfigureAwait(false);
        var text = _options.Encoding.GetString(content.GetBuffer(), 0, checked((int)content.Length));
        return (text, SplitListing(text));
    }

    /// <summary>分割数据列表行并检查单行及总条目限制，允许服务器使用 CRLF 或 LF。</summary>
    private IReadOnlyList<string> SplitListing(string text)
    {
        List<string> result = [];
        using StringReader reader = new(text);
        while (reader.ReadLine() is { } line)
        {
            if (_options.Encoding.GetByteCount(line) > _options.MaxLineLength)
                throw new FtpProtocolException("目录行超过配置的长度限制。");
            if (line.Length == 0)
                continue;
            if (result.Count >= _options.MaxListingEntries)
                throw new FtpProtocolException("目录列表超过配置的条目限制。");
            result.Add(line);
        }
        return result.AsReadOnly();
    }
}