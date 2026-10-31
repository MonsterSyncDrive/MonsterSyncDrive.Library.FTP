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

/// <summary>
/// 批量解释常见 LIST 方言。LIST 本身没有统一行格式，因此必须先识别明确的语法，再读取字段。
/// 未识别行、重复名称或不安全路径会报错，不能静默遗漏条目并让递归删除继续执行。
/// </summary>
internal static class FtpLegacyListingParser
{
    /// <summary>英语月份名称，UNIX/VMS 协议样式采用固定文化，不依赖客户端操作系统区域设置。</summary>
    private static readonly string[] MonthNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>解析整个列表，合并 VMS 续行，并利用明确的 MVS 表头解释数据集与成员列。</summary>
    /// <param name="lines">已经过会话字节数、行长和条目数预算检查的原始文本行。</param>
    /// <param name="directory">发出 LIST 时使用的目录路径。</param>
    /// <param name="timeProvider">推断 UNIX 缺省年份时使用的时间来源。</param>
    /// <returns>只读条目集合；空目录返回空集合。</returns>
    internal static IReadOnlyList<FtpItem> ParseLines(IReadOnlyList<string> lines, string directory, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(timeProvider);
        FtpPath.Validate(directory);
        var result = new List<FtpItem>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var dialect = MvsDialect.None;
        string? pendingVmsName = null;
        foreach (var raw in lines)
        {
            if (raw is null) throw Invalid("LIST 包含空行对象。");
            if (raw.Length == 0) continue;
            var line = raw.TrimStart(' ', '\t');
            if (pendingVmsName is not null)
            {
                var merged = pendingVmsName + " " + line;
                var item = ParseVms(merged, directory, pendingVmsName + "\r\n" + raw);
                if (item is not null) Add(result, names, item);
                pendingVmsName = null;
                continue;
            }
            var tokens = Tokens(line);
            if (tokens.Count == 0) continue;
            if (IsHeader(line, tokens, ref dialect)) continue;
            FtpItem? parsed;
            if (line[0] == '+') parsed = ParseEplf(line, directory);
            else if (IsUnixMode(tokens[0].Text)) parsed = ParseUnix(line, tokens, directory, timeProvider);
            else if (IsVmsName(tokens[0].Text))
            {
                if (tokens.Count == 1) { pendingVmsName = line; continue; }
                parsed = ParseVms(line, directory, raw);
            }
            else if (tokens.Any(static token => token.Text.StartsWith('*')))
                parsed = ParseOs400(line, tokens, directory);
            else if (dialect == MvsDialect.Dataset) parsed = ParseMvsDataset(line, tokens);
            else if (dialect == MvsDialect.Member) parsed = ParseMvsMember(line, tokens, directory);
            else parsed = ParseDos(line, tokens, directory);
            if (parsed is not null) Add(result, names, parsed with { RawListing = parsed.RawListing.Length == 0 ? raw : parsed.RawListing });
        }
        if (pendingVmsName is not null) throw Invalid("VMS LIST 的文件名称行没有对应的字段续行。");
        return result.AsReadOnly();
    }

    /// <summary>识别可以安全跳过的摘要和表头，并记录 MVS 的后续数据行方言。</summary>
    private static bool IsHeader(string line, IReadOnlyList<Column> tokens, ref MvsDialect dialect)
    {
        if (line.StartsWith("Total of ", StringComparison.OrdinalIgnoreCase) &&
            (line.Contains("file", StringComparison.OrdinalIgnoreCase) || line.Contains("block", StringComparison.OrdinalIgnoreCase))) return true;
        if (tokens[0].Text.Equals("total", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Count != 2 || !long.TryParse(tokens[1].Text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw Invalid("UNIX total 摘要行格式无效。");
            return true;
        }
        if (line.StartsWith("Directory ", StringComparison.OrdinalIgnoreCase) && line.Contains('[') && line.Contains(']')) return true;
        if (tokens[0].Text.Equals("Volume", StringComparison.OrdinalIgnoreCase) &&
            tokens[^1].Text.Equals("Dsname", StringComparison.OrdinalIgnoreCase))
        {
            dialect = MvsDialect.Dataset;
            return true;
        }
        if (tokens[0].Text.Equals("Name", StringComparison.OrdinalIgnoreCase) &&
            tokens.Any(static token => token.Text.Equals("VV.MM", StringComparison.OrdinalIgnoreCase)) &&
            tokens.Any(static token => token.Text.Equals("Changed", StringComparison.OrdinalIgnoreCase)))
        {
            dialect = MvsDialect.Member;
            return true;
        }
        return false;
    }

    /// <summary>解释 UNIX 权限、ACL 标记、所有者、组、设备节点和常见的月份/ISO 日期样式。</summary>
    private static FtpItem? ParseUnix(string line, IReadOnlyList<Column> columns, string directory, TimeProvider timeProvider)
    {
        if (columns.Count < 9 || !long.TryParse(columns[1].Text, NumberStyles.None, CultureInfo.InvariantCulture, out var links))
            throw Invalid("UNIX LIST 缺少链接计数、所有者或日期列。");
        var mode = columns[0].Text;
        var facts = NewFacts("unix");
        facts["unix.owner"] = columns[2].Text;
        facts["unix.group"] = columns[3].Text;
        facts["unix.links"] = columns[1].Text;
        facts["unix.type"] = mode[..1];
        var type = mode[0] switch
        {
            '-' => FtpItemType.File,
            'd' => FtpItemType.Directory,
            'l' => FtpItemType.SymbolicLink,
            _ => FtpItemType.Unknown
        };
        var dateIndex = 5;
        long? size = null;
        if (mode[0] is 'b' or 'c')
        {
            // 字符/块设备的这一列通常是 major, minor，不能把设备号误报成文件大小。
            if (columns[4].Text.EndsWith(','))
            {
                if (!TryUnsigned(columns[4].Text[..^1], out _) || !TryUnsigned(columns[5].Text, out _))
                    throw Invalid("UNIX 设备节点的主次设备号无效。");
                facts["unix.device"] = columns[4].Text + columns[5].Text;
                dateIndex = 6;
            }
            else if (columns[4].Text.Contains(','))
            {
                var device = columns[4].Text.Split(',');
                if (device.Length != 2 || !TryUnsigned(device[0], out _) || !TryUnsigned(device[1], out _))
                    throw Invalid("UNIX 设备节点的主次设备号无效。");
                facts["unix.device"] = columns[4].Text;
            }
            else if (!TryUnsigned(columns[4].Text, out _)) throw Invalid("UNIX 设备节点字段无效。");
        }
        else
        {
            if (!TryUnsigned(columns[4].Text, out var parsedSize)) throw Invalid("UNIX LIST 文件大小不是非负整数。");
            if (type == FtpItemType.File) size = parsedSize;
        }
        if (columns.Count <= dateIndex + 2) throw Invalid("UNIX LIST 缺少日期或名称。");
        var (localTime, utcTime, dateColumns) = ParseUnixTime(columns, dateIndex, timeProvider);
        var nameIndex = dateIndex + dateColumns;
        if (columns.Count <= nameIndex) throw Invalid("UNIX LIST 缺少文件名。");
        var name = line[columns[nameIndex].Start..];
        string? target = null;
        if (type == FtpItemType.SymbolicLink)
        {
            var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow < 0 || arrow == name.Length - 4 || name.IndexOf(" -> ", arrow + 4, StringComparison.Ordinal) >= 0)
                throw Invalid("UNIX 符号链接的名称和目标不能被唯一识别。");
            target = name[(arrow + 4)..];
            name = name[..arrow];
        }
        if (name is "." or "..") return null;
        return MakeItem(name, directory, type, size, localTime, utcTime, mode, target, facts, line) with
        {
            Owner = columns[2].Text,
            Group = columns[3].Text,
            LinkCount = links
        };
    }

    /// <summary>从 UNIX 日期列确定名称起点；未标时区的传统 LIST 始终保留为 Unspecified。</summary>
    private static (DateTime? LocalTime, DateTimeOffset? UtcTime, int Columns) ParseUnixTime(
        IReadOnlyList<Column> columns, int index, TimeProvider timeProvider)
    {
        if (MonthNames.Contains(columns[index].Text, StringComparer.OrdinalIgnoreCase))
        {
            if (columns.Count <= index + 2) throw Invalid("UNIX 月份日期列不完整。");
            var clockOrYear = columns[index + 2].Text;
            var hasClock = clockOrYear.Contains(':');
            var year = timeProvider.GetUtcNow().Year.ToString(CultureInfo.InvariantCulture);
            var text = columns[index].Text + " " + columns[index + 1].Text + " " + (hasClock ? year + " " : "") + clockOrYear;
            string[] formats = hasClock ? ["MMM d yyyy H:mm", "MMM d yyyy H:mm:ss"] : ["MMM d yyyy"];
            if (!DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var value))
                throw Invalid("UNIX LIST 日期或时钟无效。");
            if (hasClock && value > timeProvider.GetUtcNow().UtcDateTime.AddDays(1)) value = value.AddYears(-1);
            return (DateTime.SpecifyKind(value, DateTimeKind.Unspecified), null, 3);
        }
        if (columns.Count <= index + 1) throw Invalid("UNIX ISO 日期列不完整。");
        var iso = columns[index].Text + " " + columns[index + 1].Text;
        string[] isoFormats = ["yyyy-MM-dd H:mm", "yyyy-MM-dd H:mm:ss", "yyyy-MM-dd H:mm:ss.FFFFFFF"];
        // GNU ls 可输出超过 .NET 七位的小数秒；保留原行，时间只截断到可表示的精度。
        var point = iso.IndexOf('.');
        if (point >= 0 && iso.Length - point - 1 > 7 && iso[(point + 1)..].All(char.IsAsciiDigit)) iso = iso[..(point + 8)];
        if (!DateTime.TryParseExact(iso, isoFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw Invalid("无法识别 UNIX LIST 的日期格式。");
        if (columns.Count > index + 2 && IsNumericOffset(columns[index + 2].Text))
        {
            var zone = columns[index + 2].Text;
            var formatted = zone[..3] + ":" + zone[3..];
            if (!TimeSpan.TryParseExact(formatted[1..], "hh\\:mm", CultureInfo.InvariantCulture, out var offset) ||
                offset > TimeSpan.FromHours(14)) throw Invalid("UNIX LIST 时区偏移无效。");
            if (zone[0] == '-') offset = -offset;
            return (null, new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset).ToUniversalTime(), 3);
        }
        return (DateTime.SpecifyKind(local, DateTimeKind.Unspecified), null, 2);
    }

    /// <summary>读取 DOS/IIS 日期、DIR/SYMLINK 标记及十进制大小，不把目录的占用量当成文件大小。</summary>
    private static FtpItem? ParseDos(string line, IReadOnlyList<Column> columns, string directory)
    {
        if (columns.Count < 4) throw Invalid("无法识别 LIST 方言或 DOS 列数不足。");
        var clock = columns[1].Text;
        var typeIndex = 2;
        if (columns.Count > 4 && columns[2].Text is "AM" or "PM" or "am" or "pm") { clock += columns[2].Text; typeIndex++; }
        string[] formats = ["MM-dd-yy h:mmtt", "MM-dd-yyyy h:mmtt", "MM/dd/yy h:mmtt", "MM/dd/yyyy h:mmtt",
            "MM-dd-yy H:mm", "MM-dd-yyyy H:mm", "yyyy-MM-dd H:mm", "yyyy-MM-dd H:mm:ss"];
        if (!DateTime.TryParseExact(columns[0].Text + " " + clock, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out var modified) || columns.Count <= typeIndex + 1)
            throw Invalid("DOS LIST 日期或名称无效；未知方言不能安全猜测。");
        var marker = columns[typeIndex].Text.ToUpperInvariant();
        var type = marker switch
        {
            "<DIR>" => FtpItemType.Directory,
            "<SYMLINK>" or "<SYMLINKD>" or "<JUNCTION>" => FtpItemType.SymbolicLink,
            _ => FtpItemType.File
        };
        long? size = null;
        if (type == FtpItemType.File)
        {
            if (!TryGroupedSize(marker, out var parsedSize)) throw Invalid("DOS LIST 大小字段无效。");
            size = parsedSize;
        }
        var name = line[columns[typeIndex + 1].Start..];
        string? target = null;
        if (type == FtpItemType.SymbolicLink && name.EndsWith(']'))
        {
            var bracket = name.LastIndexOf(" [", StringComparison.Ordinal);
            if (bracket >= 0) { target = name[(bracket + 2)..^1]; name = name[..bracket]; }
        }
        if (name is "." or "..") return null;
        return MakeItem(name, directory, type, size, modified, null, null, target, NewFacts("dos"), line);
    }

    /// <summary>解析 EPLF facts；时间明确是 UNIX epoch，目录/可读取能力冲突时不推断为普通文件。</summary>
    private static FtpItem? ParseEplf(string line, string directory)
    {
        var tab = line.IndexOf('\t');
        if (tab <= 1 || line[tab - 1] != ',' || tab == line.Length - 1) throw Invalid("EPLF 缺少 facts 逗号、TAB 或名称。");
        var facts = NewFacts("eplf");
        var directoryCapability = false;
        var fileCapability = false;
        long? size = null;
        DateTimeOffset? modified = null;
        string? unique = null;
        foreach (var fact in line.AsSpan(1, tab - 1).ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var code = fact[0];
            var value = fact[1..];
            var key = "eplf." + code;
            if (!facts.TryAdd(key, value)) throw Invalid("EPLF 包含重复 fact。");
            switch (code)
            {
                case '/':
                    if (value.Length != 0) throw Invalid("EPLF 目录 fact 不能带值。");
                    directoryCapability = true;
                    break;
                case 'r':
                    if (value.Length != 0) throw Invalid("EPLF 读取 fact 不能带值。");
                    fileCapability = true;
                    break;
                case 's':
                    if (!TryUnsigned(value, out var bytes)) throw Invalid("EPLF 字节大小无效。");
                    size = bytes;
                    break;
                case 'm':
                    if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds))
                        throw Invalid("EPLF epoch 时间无效。");
                    try { modified = DateTimeOffset.UnixEpoch.AddTicks(checked((long)(seconds * TimeSpan.TicksPerSecond))); }
                    catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
                    { throw Invalid("EPLF epoch 时间超出可表示范围。"); }
                    break;
                case 'i': unique = value; break;
            }
        }
        var name = line[(tab + 1)..];
        if (name is "." or "..") return null;
        var type = directoryCapability == fileCapability ? FtpItemType.Unknown :
            directoryCapability ? FtpItemType.Directory : FtpItemType.File;
        return MakeItem(name, directory, type, type == FtpItemType.File ? size : null,
            null, modified, null, null, facts, line) with
        { UniqueId = unique };
    }

    /// <summary>解释 VMS 的版本号、块占用、时间和权限；块数不是精确文件字节数，保留在 facts 中。</summary>
    private static FtpItem? ParseVms(string line, string directory, string raw)
    {
        var columns = Tokens(line);
        if (columns.Count < 4 || !IsVmsName(columns[0].Text)) throw Invalid("VMS LIST 的名称或字段列无效。");
        var versionSeparator = columns[0].Text.LastIndexOf(';');
        var originalName = columns[0].Text;
        var baseName = originalName[..versionSeparator];
        var isDirectory = baseName.EndsWith(".DIR", StringComparison.OrdinalIgnoreCase);
        var name = isDirectory ? baseName[..^4] : originalName;
        if (name is "." or ".." or "000000") return null;
        var blocks = columns[1].Text.Split('/');
        if (blocks.Length is < 1 or > 2 || !TryUnsigned(blocks[0], out _) ||
            (blocks.Length == 2 && !TryUnsigned(blocks[1], out _))) throw Invalid("VMS LIST 块占用字段无效。");
        string[] formats = ["d-MMM-yyyy H:mm", "d-MMM-yyyy H:mm:ss", "d-MMM-yyyy H:mm:ss.FFFFFFF"];
        if (!DateTime.TryParseExact(columns[2].Text + " " + columns[3].Text, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var modified)) throw Invalid("VMS LIST 日期无效。");
        var facts = NewFacts("vms");
        facts["vms.name"] = originalName;
        facts["vms.version"] = originalName[(versionSeparator + 1)..];
        facts["vms.blocks.used"] = blocks[0];
        if (blocks.Length == 2) facts["vms.blocks.allocated"] = blocks[1];
        string? owner = null;
        string? group = null;
        string? permissions = null;
        foreach (var column in columns.Skip(4))
        {
            if (column.Text.StartsWith('[') && column.Text.EndsWith(']'))
            {
                facts["vms.owner"] = column.Text;
                var identity = column.Text[1..^1].Split(',');
                if (identity.Length == 2) { group = identity[0]; owner = identity[1]; }
                else owner = column.Text[1..^1];
            }
            else if (column.Text.StartsWith('(') && column.Text.EndsWith(')')) permissions = column.Text;
        }
        var item = MakeItem(name, directory, isDirectory ? FtpItemType.Directory : FtpItemType.File,
            null, modified, null, permissions, null, facts, raw) with
        { Owner = owner, Group = group };
        // VMS 原生目录使用 DISK:[A.B]FILE;1，而不是 DISK:[A.B]/FILE;1。
        // 只有调用者已经提供明确的方括号目录语法时才使用原生拼接；否则遵循既有 FTP 路径规则。
        if (directory.EndsWith(']') && directory.Contains('['))
        {
            var native = isDirectory ? directory[..^1] + (directory.EndsWith(".]", StringComparison.Ordinal) ? "" : ".") + name + "]" :
                directory + originalName;
            item = item with { FullPath = native };
        }
        return item;
    }

    /// <summary>解析 IBM i/OS400 的 owner size date time type name 及省略大小日期的成员行。</summary>
    private static FtpItem? ParseOs400(string line, IReadOnlyList<Column> columns, string directory)
    {
        var typeIndex = -1;
        for (var index = 1; index < columns.Count; index++)
        {
            if (columns[index].Text.StartsWith('*')) { typeIndex = index; break; }
        }
        if (typeIndex < 1) throw Invalid("OS400 LIST 类型或名称缺失。");
        // 列举一个物理文件的成员时，IBM i 会先输出当前 *FILE 自身的无名称统计行。
        // 这不是子项，必须明确识别后跳过；其他缺名称行仍是协议错误。
        if (columns.Count == typeIndex + 1 && typeIndex == 4 && columns[typeIndex].Text.Equals("*FILE", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryUnsigned(columns[1].Text, out _)) throw Invalid("OS400 当前文件统计大小无效。");
            _ = ParseUnambiguousLocalTime(columns[2].Text, columns[3].Text);
            return null;
        }
        if (columns.Count <= typeIndex + 1) throw Invalid("OS400 LIST 类型或名称缺失。");
        var marker = columns[typeIndex].Text.ToUpperInvariant();
        var rawName = line[columns[typeIndex + 1].Start..];
        var type = marker switch
        {
            "*DIR" or "*LIB" or "*DDIR" => FtpItemType.Directory,
            "*STMF" or "*MEM" => FtpItemType.File,
            "*SYMLNK" => FtpItemType.SymbolicLink,
            "*FILE" when rawName.EndsWith(".SAVF", StringComparison.OrdinalIgnoreCase) => FtpItemType.File,
            _ => FtpItemType.Unknown
        };
        var name = type == FtpItemType.Directory ? rawName.TrimEnd('/') : rawName;
        // 某些 OS400 列表在根目录给子项增加一个 '/'。仅在已明确请求根目录时去掉它，
        // 不把任意绝对路径取 basename，否则恶意列表可以引导递归操作离开所请求目录。
        if (directory == "/" && name.StartsWith('/')) name = name[1..];
        if (name is "." or "..") return null;
        var facts = NewFacts("os400");
        facts["os400.type"] = marker;
        facts["os400.owner"] = columns[0].Text;
        long? size = null;
        DateTime? modified = null;
        if (typeIndex == 4)
        {
            if (!TryUnsigned(columns[1].Text, out var bytes)) throw Invalid("OS400 LIST 大小字段无效。");
            if (type == FtpItemType.File && marker != "*MEM") size = bytes;
            facts["os400.date"] = columns[2].Text;
            facts["os400.time"] = columns[3].Text;
            modified = ParseUnambiguousLocalTime(columns[2].Text, columns[3].Text);
        }
        else if (typeIndex != 1) throw Invalid("OS400 LIST 字段布局未知，不能安全解释。");
        return MakeItem(name, directory, type, size, modified, null, null, null, facts, line) with { Owner = columns[0].Text };
    }

    /// <summary>解释由明确表头确认的 MVS 数据集行；磁道/记录占用只保留事实，不当作字节大小。</summary>
    private static FtpItem ParseMvsDataset(string line, IReadOnlyList<Column> columns)
    {
        if (columns.Count < 2) throw Invalid("MVS LIST 数据集行字段不足。");
        var name = columns[^1].Text;
        var facts = NewFacts("mvs-dataset");
        var type = FtpItemType.Unknown;
        if (columns.Count >= 9)
        {
            var organization = columns[^2].Text.ToUpperInvariant();
            type = organization switch
            {
                "PS" or "PSU" => FtpItemType.File,
                "PO" or "PO-E" or "POU" => FtpItemType.Directory,
                _ => FtpItemType.Unknown
            };
            facts["mvs.dsorg"] = organization;
            facts["mvs.recfm"] = columns[^5].Text;
            facts["mvs.lrecl"] = columns[^4].Text;
            facts["mvs.blksize"] = columns[^3].Text;
            facts["mvs.volume"] = columns[0].Text;
            // Referred 是最后引用日期，不是最后修改日期，不能填入 ModifiedTime。
            var reference = columns.FirstOrDefault(static column => column.Text.Count(static value => value == '/') == 2);
            if (reference.Text is not null) facts["mvs.referred"] = reference.Text;
        }
        else if (!columns[0].Text.Equals("Migrated", StringComparison.OrdinalIgnoreCase) &&
                 !columns[0].Text.Equals("VSAM", StringComparison.OrdinalIgnoreCase))
            throw Invalid("MVS 数据集行既非标准列也非明确的迁移/VSAM条目。");
        ValidateMvsName(name, dataset: true);
        return new FtpItem
        {
            Name = name,
            FullPath = "'" + name.Trim('\'') + "'",
            Type = type,
            RawListing = line,
            Facts = new ReadOnlyDictionary<string, string>(facts)
        };
    }

    /// <summary>解释 MVS PDS 成员；Size 列是记录数而非字节数，保留 mvs.records。</summary>
    private static FtpItem ParseMvsMember(string line, IReadOnlyList<Column> columns, string directory)
    {
        if (columns.Count < 1) throw Invalid("MVS 成员名称缺失。");
        var name = columns[0].Text;
        ValidateMvsName(name, dataset: false);
        var facts = NewFacts("mvs-member");
        DateTime? modified = null;
        string? owner = null;
        if (columns.Count > 1)
        {
            if (columns.Count < 9 || !TryUnsigned(columns[5].Text, out _)) throw Invalid("MVS 成员统计列无效。");
            facts["mvs.version"] = columns[1].Text;
            facts["mvs.created"] = columns[2].Text;
            facts["mvs.records"] = columns[5].Text;
            facts["mvs.initial-records"] = columns[6].Text;
            facts["mvs.modified-records"] = columns[7].Text;
            owner = columns[8].Text;
            if (!DateTime.TryParseExact(columns[3].Text + " " + columns[4].Text,
                new[] { "yyyy/MM/dd H:mm:ss", "yyyy/MM/dd H:mm", "yyyy-MM-dd H:mm:ss" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw Invalid("MVS 成员修改时间无效。");
            modified = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        }
        if (!directory.StartsWith('/') && directory != ".") ValidateMvsName(directory.Trim('\''), dataset: true);
        var fullPath = directory == "." ? name : directory.StartsWith('/') ? FtpPath.Combine(directory, name) :
            "'" + directory.Trim('\'') + "(" + name + ")'";
        return new FtpItem
        {
            Name = name,
            FullPath = fullPath,
            Type = FtpItemType.File,
            ListingModifiedTime = modified,
            Owner = owner,
            RawListing = line,
            Facts = new ReadOnlyDictionary<string, string>(facts)
        };
    }

    /// <summary>构造通用条目并统一验证不可穿越的名称；缺少的元数据保持空值。</summary>
    private static FtpItem MakeItem(string name, string directory, FtpItemType type, long? size, DateTime? local,
        DateTimeOffset? utc, string? permissions, string? target, Dictionary<string, string> facts, string raw)
    {
        ValidateName(name);
        return new FtpItem
        {
            Name = name,
            FullPath = FtpPath.Combine(directory, name),
            Type = type,
            Size = size,
            ListingModifiedTime = local.HasValue ? DateTime.SpecifyKind(local.Value, DateTimeKind.Unspecified) : null,
            ModifiedTime = utc,
            Permissions = permissions,
            LinkTarget = target,
            Facts = new ReadOnlyDictionary<string, string>(facts),
            RawListing = raw
        };
    }

    /// <summary>增加已解释的条目；同一列表中的重名行无法安全决定应操作哪个条目，因此拒绝。</summary>
    private static void Add(List<FtpItem> result, HashSet<string> names, FtpItem item)
    {
        if (!names.Add(item.Name)) throw Invalid("LIST 返回了重复子项名称。");
        result.Add(item);
    }

    /// <summary>创建大小写不敏感的方言事实字典，至少记录解释器识别到的方言。</summary>
    private static Dictionary<string, string> NewFacts(string format) => new(StringComparer.OrdinalIgnoreCase) { ["legacy.format"] = format };

    /// <summary>将不安全的服务器名称统一转换为协议错误，避免把服务器内容当成调用者参数错误。</summary>
    private static void ValidateName(string name)
    {
        try { FtpPath.ValidateChildName(name); }
        catch (ArgumentException) { throw Invalid("LIST 名称包含控制字符或空名称。"); }
    }

    /// <summary>验证可安全放入 z/OS 单引号数据集语法的限定名或成员名，禁止额外引号和通配符。</summary>
    private static void ValidateMvsName(string name, bool dataset)
    {
        ValidateName(name);
        if (name.Length > (dataset ? 44 : 8)) throw Invalid("MVS 数据集或成员名超出长度限制。");
        var qualifiers = dataset ? name.Split('.') : new[] { name };
        foreach (var qualifier in qualifiers)
        {
            if (qualifier.Length is < 1 or > 8 || !(char.IsAsciiLetter(qualifier[0]) || qualifier[0] is '@' or '#' or '$') ||
                qualifier.Any(static value => !(char.IsAsciiLetterOrDigit(value) || value is '@' or '#' or '$' or '-')))
                throw Invalid("MVS 数据集或成员名包含无法安全解释的字符。");
        }
    }

    /// <summary>识别十位 UNIX 模式及可选的 ACL/安全上下文标记。</summary>
    private static bool IsUnixMode(string text)
    {
        if (text.Length is < 10 or > 11 || text[0] is not ('-' or 'd' or 'l' or 'b' or 'c' or 'p' or 's' or 'D')) return false;
        if (text.Length == 11 && text[10] is not ('+' or '@' or '.')) return false;
        for (var index = 1; index <= 9; index++)
        {
            var allowed = (index % 3) switch { 1 => "r-", 2 => "w-", _ => index == 9 ? "xtT-" : "xsS-" };
            if (!allowed.Contains(text[index])) return false;
        }
        return true;
    }

    /// <summary>识别带正整数版本后缀的 VMS 名称。</summary>
    private static bool IsVmsName(string name)
    {
        var semicolon = name.LastIndexOf(';');
        return semicolon > 0 && int.TryParse(name.AsSpan(semicolon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version > 0;
    }

    /// <summary>识别明确的 +HHMM / -HHMM 时区字段。</summary>
    private static bool IsNumericOffset(string text) => text.Length == 5 && text[0] is '+' or '-' && text.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>读取非负十进制 64 位整数，不接受符号、千位分隔符或空白。</summary>
    private static bool TryUnsigned(string text, out long value) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// <summary>读取可选的规范逗号千位分组；不接受 1,2 或连续逗号等模糊数字。</summary>
    private static bool TryGroupedSize(string text, out long value)
    {
        if (!text.Contains(',')) return TryUnsigned(text, out value);
        var parts = text.Split(',');
        if (parts[0].Length is < 1 or > 3 || parts.Any(static part => part.Length == 0 || !part.All(char.IsAsciiDigit)) ||
            parts.Skip(1).Any(static part => part.Length != 3)) { value = 0; return false; }
        return TryUnsigned(string.Concat(parts), out value);
    }

    /// <summary>解析 OS400 常见日期；月/日有歧义且无配置可消除时保留空时间及原始 facts。</summary>
    private static DateTime? ParseUnambiguousLocalTime(string date, string clock)
    {
        string[] formats = ["dd.MM.yy H:mm:ss", "dd.MM.yyyy H:mm:ss", "MM/dd/yy H:mm:ss", "dd/MM/yy H:mm:ss",
            "MM/dd/yyyy H:mm:ss", "dd/MM/yyyy H:mm:ss", "yyyy-MM-dd H:mm:ss", "yyyy/MM/dd H:mm:ss",
            "yy/MM/dd H:mm:ss", "dd.MM.yy H:mm", "MM/dd/yy H:mm", "dd/MM/yy H:mm", "yy/MM/dd H:mm"];
        DateTime? result = null;
        foreach (var format in formats)
        {
            if (!DateTime.TryParseExact(date + " " + clock, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)) continue;
            if (result.HasValue && result != value) return null;
            result = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        }
        if (!result.HasValue) throw Invalid("OS400 LIST 日期或时间无效。");
        return result;
    }

    /// <summary>按 ASCII 空格/TAB 读取字段并保留每列起点，以便无损保留文件名内部空格。</summary>
    private static List<Column> Tokens(string line)
    {
        var result = new List<Column>();
        var index = 0;
        while (index < line.Length)
        {
            while (index < line.Length && line[index] is ' ' or '\t') index++;
            if (index == line.Length) break;
            var start = index;
            while (index < line.Length && line[index] is not (' ' or '\t')) index++;
            result.Add(new Column(line[start..index], start));
        }
        return result;
    }

    /// <summary>创建不包含不可信原始服务器文本的格式错误异常。</summary>
    private static FtpProtocolException Invalid(string message) => new(message);

    /// <summary>MVS 表头已经确定的后续行语义。</summary>
    private enum MvsDialect
    {
        /// <summary>尚未看到明确的 MVS 表头。</summary>
        None,
        /// <summary>完整数据集表。</summary>
        Dataset,
        /// <summary>PDS 成员统计表。</summary>
        Member
    }

    /// <summary>一个字段及其原行起点；只保存解析需要的信息。</summary>
    /// <param name="Text">字段原文。</param>
    /// <param name="Start">原行中的零基起点。</param>
    private readonly record struct Column(string Text, int Start);
}