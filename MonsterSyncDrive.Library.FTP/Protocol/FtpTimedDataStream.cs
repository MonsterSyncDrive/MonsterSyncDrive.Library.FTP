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

namespace MonsterSyncDrive.Library.FTP.Protocol;

/// <summary>对每次底层网络 I/O 施加空闲超时；结构化记录/页可以跨多个读取，不受单个记录总时长误限。</summary>
internal sealed class FtpTimedDataStream : Stream
{
    /// <summary>原始 TCP/TLS 明文视图，由客户端管理生命周期。</summary>
    private readonly Stream _inner;

    /// <summary>单次网络 I/O 的空闲期限。</summary>
    private readonly TimeSpan _timeout;

    /// <summary>只取消本次数据事务的标记，ABOR 控制恢复使用其他标记。</summary>
    private readonly CancellationToken _operationToken;

    /// <summary>创建不拥有底层流的超时视图。</summary>
    internal FtpTimedDataStream(Stream inner, TimeSpan timeout, CancellationToken operationToken)
    {
        _inner = inner;
        _timeout = timeout;
        _operationToken = operationToken;
    }

    /// <summary>底层是否可读。</summary>
    public override bool CanRead => _inner.CanRead;
    /// <summary>网络流不支持定位。</summary>
    public override bool CanSeek => false;
    /// <summary>底层是否可写。</summary>
    public override bool CanWrite => _inner.CanWrite;
    /// <summary>网络流不提供总长度。</summary>
    public override long Length => throw new NotSupportedException();
    /// <summary>网络流不提供位置。</summary>
    public override long Position
    {
        get => throw new NotSupportedException(); set => throw new NotSupportedException();
    }

    /// <summary>异步读取底层字节，将单次超时与调用者/ABOR取消区分。</summary>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_operationToken, cancellationToken);
        return await FtpTimeout.RunAsync(token => _inner.ReadAsync(buffer, token).AsTask(), _timeout, linked.Token, "网络数据读取")
            .ConfigureAwait(false);
    }

    /// <summary>异步写入底层字节并限制单次 I/O 时间。</summary>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_operationToken, cancellationToken);
        await FtpTimeout.RunAsync(token => _inner.WriteAsync(buffer, token).AsTask(), _timeout, linked.Token, "网络数据写入")
            .ConfigureAwait(false);
    }

    /// <summary>异步刷新底层流。</summary>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_operationToken, cancellationToken);
        await FtpTimeout.RunAsync(_inner.FlushAsync, _timeout, linked.Token, "网络数据刷新").ConfigureAwait(false);
    }

    /// <summary>兼容数组读取入口。</summary>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>兼容数组写入入口。</summary>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>同步读取适配。</summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>同步写入适配。</summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>同步刷新适配。</summary>
    public override void Flush()
    {
        FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>不支持定位。</summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    /// <summary>不支持改变长度。</summary>
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }
}