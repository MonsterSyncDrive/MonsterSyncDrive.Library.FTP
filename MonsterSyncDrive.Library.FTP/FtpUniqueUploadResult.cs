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

/// <summary>STOU 上传结果，文件名由服务器生成，且只有读到最终成功回复才返回此结果。</summary>
public sealed record FtpUniqueUploadResult
{
    /// <summary>服务器原样返回的唯一文件名或路径，不执行 URI 解码。</summary>
    public required string RemotePath
    {
        get; init;
    }

    /// <summary>本次从调用者源流读取的字节数。</summary>
    public long BytesTransferred
    {
        get; init;
    }

    /// <summary>包含服务器选择名称的预备回复。</summary>
    public required FtpReply PreliminaryReply
    {
        get; init;
    }

    /// <summary>传输最终成功回复。</summary>
    public required FtpReply CompletionReply
    {
        get; init;
    }
}