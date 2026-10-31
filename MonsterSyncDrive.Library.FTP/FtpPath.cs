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

namespace MonsterSyncDrive.Library.FTP;

/// <summary>与操作系统无关的 FTP 路径工具。使用正斜杠，不进行 URI 转义或本地路径转换。</summary>
public static class FtpPath
{
    /// <summary>验证远程路径，保留空格、百分号和 Unicode；拒绝命令注入字符。</summary>
    public static string Validate(string path)
    {
        ValidateArgument(path, nameof(path));
        return path;
    }

    /// <summary>合并目录与单个子项名称。拒绝目录穿越、斜杠和点目录。</summary>
    public static string Combine(string directory, string name)
    {
        _ = Validate(directory);
        ValidateChildName(name);
        return directory.TrimEnd('/') + "/" + name;
    }

    /// <summary>取得最后一个路径段；根目录返回空字符串。</summary>
    public static string GetFileName(string path)
    {
        _ = Validate(path);
        string trimmed = path.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    /// <summary>取得父目录；无目录前缀时返回点目录，绝对根路径返回根目录。</summary>
    public static string GetDirectoryName(string path)
    {
        _ = Validate(path);
        string trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
            return "/";
        int slash = trimmed.LastIndexOf('/');
        return slash < 0 ? "." : slash == 0 ? "/" : trimmed[..slash];
    }

    /// <summary>校验一个不可分割的列表子项，防止递归操作被恶意列表引向其他目录。</summary>
    internal static void ValidateChildName(string name)
    {
        ValidateArgument(name, nameof(name));
        if (name is "." or ".." || name.IndexOfAny(['/', '\\']) >= 0)
            throw new FtpProtocolException("目录列表包含不安全的子项名称。");
    }

    /// <summary>验证 FTP 参数。控制通道按 CRLF 分帧，因此禁止所有 ASCII 控制字符及 DEL。</summary>
    internal static void ValidateArgument(string value, string name, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (!allowEmpty && value.Length == 0)
            throw new ArgumentException("FTP 参数不可为空。", name);
        foreach (char character in value)
        {
            if (character is < ' ' or '\u007f')
                throw new ArgumentException("FTP 参数不可包含控制字符。", name);
        }
    }

    /// <summary>阻止根目录和含点目录段的递归删除，避免服务器对别名路径的特殊解释。</summary>
    internal static void ValidateRecursiveTarget(string path)
    {
        _ = Validate(path);
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(static part => part is "." or "..") || path.Contains('\\'))
            throw new ArgumentException("递归操作必须指向明确的非根目录，且不可包含点目录或反斜杠。", nameof(path));
    }
}