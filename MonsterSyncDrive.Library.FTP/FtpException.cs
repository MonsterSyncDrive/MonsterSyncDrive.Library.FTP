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

/// <summary>FTP 网络或协议错误的基础异常。</summary>
public class FtpException : IOException
{
    /// <summary>使用说明创建异常。</summary>
    public FtpException(string message) : base(message) { }

    /// <summary>保留底层网络、编码或 TLS 异常。</summary>
    public FtpException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>服务器返回与命令预期不符的回复；Command 仅保存动词，避免泄露口令或路径。</summary>
/// <remarks>创建包含命令名和服务器回复的错误。</remarks>
public sealed class FtpCommandException(string command, FtpReply reply) : FtpException($"FTP {command} 返回状态 {reply.Code}。")
{

    /// <summary>命令动词，例如 RETR；从不包含 PASS/ACCT 参数。</summary>
    public string Command { get; } = command;

    /// <summary>服务器的完整回复；服务器文本可能包含敏感信息，由调用者决定是否记录。</summary>
    public FtpReply Reply { get; } = reply;

    /// <summary>是否是 4xx 暂时性失败；不会自动重试有副作用的操作。</summary>
    public bool IsTransient => Reply.Code is >= 400 and < 500;
}

/// <summary>格式错误、截断回复、越界端点或列表无法安全解释等协议错误。</summary>
public sealed class FtpProtocolException : FtpException
{
    /// <summary>使用协议错误说明创建异常。</summary>
    public FtpProtocolException(string message) : base(message) { }
}