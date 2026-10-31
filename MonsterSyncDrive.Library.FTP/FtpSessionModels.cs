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

/// <summary>服务器 SITE 多步操作中的一个命令。整组命令独占会话，防止其他操作消耗中间状态。</summary>
public sealed record FtpSiteCommand
{
    /// <summary>SITE 后的完整参数，例如 CPFR /source 或 CPTO /destination；不能包含控制字符。</summary>
    public required string Arguments
    {
        get; init;
    }

    /// <summary>此步骤是否必须返回 3xx 中间回复。最后一步必须为 false，并返回最终 2xx 回复。</summary>
    public bool RequiresContinuation
    {
        get; init;
    }
}

/// <summary>RFC 3659 perm fact 表示的当前用户操作权限。它不同于 UNIX 文件模式位。</summary>
[Flags]
public enum FtpPermissions
{
    /// <summary>服务器明确给出空 perm fact，表示没有已公布的操作权限。</summary>
    None = 0,
    /// <summary>a：允许向现有文件追加数据。</summary>
    Append = 1,
    /// <summary>c：允许在目录中创建文件。</summary>
    CreateFile = 2,
    /// <summary>d：允许删除当前条目。</summary>
    Delete = 4,
    /// <summary>e：允许进入目录。</summary>
    EnterDirectory = 8,
    /// <summary>f：允许重命名当前条目。</summary>
    Rename = 16,
    /// <summary>l：允许列举目录。</summary>
    ListDirectory = 32,
    /// <summary>m：允许在目录中创建子目录。</summary>
    CreateDirectory = 64,
    /// <summary>p：允许删除目录的子条目。</summary>
    DeleteChildren = 128,
    /// <summary>r：允许读取文件。</summary>
    Read = 256,
    /// <summary>w：允许写入文件。</summary>
    Write = 512
}