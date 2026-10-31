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
using System.Globalization;

namespace MonsterSyncDrive.Library.FTP.Protocol;

/// <summary>解析 MLSD/MLST facts 及 UTC 时间，保留服务器的扩展字段。</summary>
internal static class FtpListingParser
{
    /// <summary>解析 MLSD/MLST 行；cdir/pdir 返回空，目录列表的子项必须是不可穿越的单一名称。</summary>
    internal static FtpItem? ParseMachine(string line, string directory, bool singleItem = false)
    {
        // MLST 在事实行之前有一个结构性的 SP，空 facts 则紧接着另一个 SP。
        // 不能 TrimStart：这会吞掉空字段分隔符，也会破坏文件名的前导空格。
        var text = singleItem && line.StartsWith(' ') ? line[1..] : line;
        var separator = text.IndexOf(' ');
        if (separator < 0 || (separator > 0 && text[separator - 1] != ';') || separator == text.Length - 1)
            throw new FtpProtocolException("MLSD/MLST 行缺少 facts 分隔符或名称。");
        var facts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var factParts = separator == 0 ? Array.Empty<string>() : text[..(separator - 1)].Split(';');
        foreach (var part in factParts)
        {
            var equals = part.IndexOf('=');
            if (equals <= 0 || part[..equals].Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_') ||
                !facts.TryAdd(part[..equals], part[(equals + 1)..]))
                throw new FtpProtocolException("MLSD/MLST 包含无效或重复的 fact。");
        }
        facts.TryGetValue("type", out var type);
        if (type is not null && (type.Equals("cdir", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("pdir", StringComparison.OrdinalIgnoreCase))) return null;
        var name = text[(separator + 1)..];
        if (!singleItem) FtpPath.ValidateChildName(name);
        var itemType = type?.ToLowerInvariant() switch
        {
            "file" => FtpItemType.File,
            "dir" => FtpItemType.Directory,
            { } value when value.StartsWith("os.unix=slink", StringComparison.Ordinal) ||
                value.StartsWith("os.unix=symlink", StringComparison.Ordinal) => FtpItemType.SymbolicLink,
            _ => FtpItemType.Unknown
        };
        long? size = null;
        if (facts.TryGetValue("size", out var sizeText))
        {
            if (!long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                throw new FtpProtocolException("MLSD/MLST size 不是非负 64 位整数。");
            size = parsed;
        }
        DateTimeOffset? modified = null;
        if (facts.TryGetValue("modify", out var time)) modified = ParseTimestamp(time);
        DateTimeOffset? created = null;
        if (facts.TryGetValue("create", out var creation)) created = ParseTimestamp(creation);
        facts.TryGetValue("unix.mode", out var permissions);
        if (permissions is not null && (permissions.Length == 0 || permissions.Any(static c => c is < '0' or > '7')))
            throw new FtpProtocolException("MLSD/MLST UNIX.mode 不是有效八进制权限。");
        FtpPermissions? allowed = facts.TryGetValue("perm", out var permissionText) ? ParsePermissions(permissionText) : null;
        var linkColon = type?.IndexOf(':') ?? -1;
        return new FtpItem
        {
            Name = singleItem ? FtpPath.GetFileName(name) : name,
            FullPath = singleItem ? directory : FtpPath.Combine(directory, name),
            Type = itemType,
            Size = size,
            ModifiedTime = modified,
            CreatedTime = created,
            Permissions = permissions,
            Owner = GetFact(facts, "unix.ownername") ?? GetFact(facts, "unix.owner"),
            Group = GetFact(facts, "unix.groupname") ?? GetFact(facts, "unix.group"),
            UserId = ParseNonNegativeFact(facts, "unix.uid"),
            GroupId = ParseNonNegativeFact(facts, "unix.gid"),
            LinkCount = ParseNonNegativeFact(facts, "unix.nlink"),
            UniqueId = GetFact(facts, "unique"),
            AllowedOperations = allowed,
            CharacterSet = GetFact(facts, "charset"),
            Language = GetFact(facts, "lang"),
            MediaType = GetFact(facts, "media-type"),
            LinkTarget = itemType == FtpItemType.SymbolicLink && linkColon >= 0 ? type![(linkColon + 1)..] : null,
            RawListing = line,
            Facts = new ReadOnlyDictionary<string, string>(facts)
        };
    }

    /// <summary>取得不透明 fact 的原值；缺失字段与明确空字符串保持区别。</summary>
    private static string? GetFact(IReadOnlyDictionary<string, string> facts, string key) =>
        facts.TryGetValue(key, out var value) ? value : null;

    /// <summary>严格解析系统扩展的非负整数，拒绝负数、溢出和带分隔符的数字。</summary>
    private static long? ParseNonNegativeFact(IReadOnlyDictionary<string, string> facts, string key)
    {
        if (!facts.TryGetValue(key, out var value)) return null;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw new FtpProtocolException("MLSD/MLST 的 " + key + " 不是非负 64 位整数。");
        return number;
    }

    /// <summary>按 RFC 3659 解释 perm 字母；未知扩展字母保留在 Facts 中，不将未知权限授予调用者。</summary>
    private static FtpPermissions ParsePermissions(string value)
    {
        var result = FtpPermissions.None;
        foreach (var permission in value)
        {
            if (!char.IsAsciiLetter(permission)) throw new FtpProtocolException("MLSD/MLST perm 包含非法字符。");
            result |= char.ToLowerInvariant(permission) switch
            {
                'a' => FtpPermissions.Append,
                'c' => FtpPermissions.CreateFile,
                'd' => FtpPermissions.Delete,
                'e' => FtpPermissions.EnterDirectory,
                'f' => FtpPermissions.Rename,
                'l' => FtpPermissions.ListDirectory,
                'm' => FtpPermissions.CreateDirectory,
                'p' => FtpPermissions.DeleteChildren,
                'r' => FtpPermissions.Read,
                'w' => FtpPermissions.Write,
                _ => FtpPermissions.None
            };
        }
        return result;
    }

    /// <summary>解析 RFC 3659 UTC 时间，允许小数秒；超出 .NET 精度的小数截断至七位。</summary>
    internal static DateTimeOffset ParseTimestamp(string text)
    {
        if (text.Length < 14 || (text.Length > 14 && (text[14] != '.' || text.Length == 15)) ||
            text.Where((c, index) => index != 14).Any(static c => !char.IsAsciiDigit(c)))
            throw new FtpProtocolException("服务器返回了无效的 UTC 文件时间。");
        var normalized = text.Length > 22 ? text[..22] : text;
        if (!DateTimeOffset.TryParseExact(normalized, normalized.Length == 14 ? "yyyyMMddHHmmss" :
            "yyyyMMddHHmmss." + new string('f', normalized.Length - 15), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            throw new FtpProtocolException("服务器返回了超出范围的 UTC 文件时间。");
        return value;
    }
}