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

/// <summary>一次完整的 FTP 回复，包括多行回复的开始行、中间行和结束行。</summary>
public sealed class FtpReply
{
    /// <summary>创建不可变回复并复制行集合。</summary>
    /// <param name="code">FTP 三位回复码，范围 100 至 699；6xx 用于 RFC2228 受保护回复。</param>
    /// <param name="lines">原始回复行，不含 CRLF。</param>
    public FtpReply(int code, IEnumerable<string> lines)
    {
        if (code is < 100 or > 699) throw new ArgumentOutOfRangeException(nameof(code));
        ArgumentNullException.ThrowIfNull(lines);
        Code = code;
        Lines = Array.AsReadOnly(lines.ToArray());
        if (Lines.Count == 0) throw new ArgumentException("回复至少包含一行。", nameof(lines));
    }

    /// <summary>三位数字回复码。</summary>
    public int Code { get; }

    /// <summary>原始回复行，只读且保持服务器顺序。</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>最后一行除去状态码和分隔符的文本。</summary>
    public string Message => Lines[^1].Length > 4 ? Lines[^1][4..] : string.Empty;

    /// <summary>是否是 1xx 预备回复；此类回复后还必须读取完成回复。</summary>
    public bool IsPreliminary => Code is >= 100 and < 200;

    /// <summary>是否是 2xx 完成回复。</summary>
    public bool IsSuccess => Code is >= 200 and < 300;

    /// <summary>是否是 3xx 中间回复，表示需要后续命令。</summary>
    public bool IsIntermediate => Code is >= 300 and < 400;

    /// <summary>是否是 4xx 或 5xx 错误回复。</summary>
    public bool IsError => Code is >= 400 and < 600;

    /// <summary>生成完整回复文本，便于诊断。</summary>
    public override string ToString() => string.Join(Environment.NewLine, Lines);
}