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

/// <summary>为一个网络阶段施加可取消的截止时间，并区分调用者取消与真正超时。</summary>
internal static class FtpTimeout
{
    /// <summary>运行有返回值的网络阶段。内部超时转换为 TimeoutException，调用者取消保持 OperationCanceledException。</summary>
    internal static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, TimeSpan timeout,
        CancellationToken cancellationToken, string description)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
            deadline.CancelAfter(timeout);
        try
        {
            return await operation(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"FTP {description}超时。", exception);
        }
    }

    /// <summary>运行无返回值的网络阶段，并复用统一的超时异常转换。</summary>
    internal static Task RunAsync(Func<CancellationToken, Task> operation, TimeSpan timeout,
        CancellationToken cancellationToken, string description)
    {
        return RunAsync(async token =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        },
            timeout, cancellationToken, description);
    }
}