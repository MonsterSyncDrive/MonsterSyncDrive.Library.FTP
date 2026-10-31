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

/// <summary>显式 ABOR 已结束数据操作且控制会话已重新同步；客户端仍可继续发送命令。</summary>
public sealed class FtpTransferAbortedException : FtpException
{
    /// <summary>保存完整 ABOR 回复，便于判断是主动中止还是传输恰好已经完成。</summary>
    public FtpTransferAbortedException(FtpAbortResult result)
        : base("FTP 数据传输已由 ABOR 中止，控制会话仍可使用。")
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    /// <summary>中止事务的结果及服务器原始回复。</summary>
    public FtpAbortResult Result { get; }
}

/// <summary>ABOR 执行选项。默认带内命令适用于明文、TLS 和 RFC 2228 保护通道。</summary>
public sealed record FtpAbortOptions
{
    /// <summary>
    /// 明文控制连接是否先发送 Telnet IP/SYNCH（TCP urgent Data Mark）。
    /// 仅用于要求该历史机制的服务器；TLS 或受保护命令通道禁止该选项。
    /// </summary>
    public bool SendTelnetInterrupt { get; init; }
}

/// <summary>已完成并通过 NOOP 屏障确认同步的 ABOR 事务。</summary>
public sealed class FtpAbortResult
{
    /// <summary>复制回复集合，防止外部修改已确认的协议结果。</summary>
    internal FtpAbortResult(bool wasTransferring, bool usedTelnetInterrupt, IEnumerable<FtpReply> replies)
    {
        WasTransferring = wasTransferring;
        UsedTelnetInterrupt = usedTelnetInterrupt;
        Replies = Array.AsReadOnly(replies.ToArray());
    }

    /// <summary>请求中止时是否确有一个已收到 125/150 的数据命令。</summary>
    public bool WasTransferring { get; }

    /// <summary>是否使用了明文 Telnet IP/SYNCH 通知。</summary>
    public bool UsedTelnetInterrupt { get; }

    /// <summary>ABOR 及其之前数据命令的最终回复，不包含内部 NOOP 屏障回复。</summary>
    public IReadOnlyList<FtpReply> Replies { get; }
}