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

using System.Net.Security;

namespace MonsterSyncDrive.Library.FTP;

/// <summary>数据通道 TLS 会话恢复策略。</summary>
public enum FtpTlsSessionReusePolicy
{
    /// <summary>禁止平台进行 TLS 会话恢复。</summary>
    Disabled,
    /// <summary>允许平台尽力恢复，未验证的恢复状态保持未知。</summary>
    Allow,
    /// <summary>要求提供器证明数据会话复用了指定控制会话；默认 SslStream 无此可验证公开 API。</summary>
    RequireControlSessionReuse
}

/// <summary>一次 TLS 认证的参数以及可选的控制会话恢复上下文。</summary>
public sealed record FtpTlsAuthenticationContext
{
    /// <summary>尚未加密的 TCP 流。</summary>
    public required Stream Transport { get; init; }

    /// <summary>主机名、证书验证、吊销与协议策略；自定义提供器必须遵守这些策略。</summary>
    public required SslClientAuthenticationOptions AuthenticationOptions { get; init; }

    /// <summary>此连接是否是数据通道；主动模式的数据通道也由本端作为 TLS 客户端。</summary>
    public bool IsDataChannel { get; init; }

    /// <summary>数据通道对应的控制 TLS 会话；CCC 之后其流可能已关闭，但恢复状态仍可用。</summary>
    public FtpTlsSession? ControlSession { get; init; }

    /// <summary>释放 TLS 包装流时是否必须保留底层 TCP，控制连接降级 CCC/REIN 需要此能力。</summary>
    public bool LeaveTransportOpen { get; init; }
}

/// <summary>
/// TLS 提供器，可在具备公开原生 TLS 会话 API 的宿主实现严格的控制/数据会话复用。
/// 实现必须返回真实的 TLS 流、遵守认证策略，且只能在实际验证绑定后将 IsControlSessionReused 置为 true。
/// </summary>
public interface IFtpTlsProvider
{
    /// <summary>建立一个 TLS 客户端会话；失败时抛异常，不得改用明文。</summary>
    Task<FtpTlsSession> AuthenticateAsync(FtpTlsAuthenticationContext context, CancellationToken cancellationToken);
}

/// <summary>TLS 认证结果；可空恢复状态明确区分“不支持观测”和“已验证没有恢复”。</summary>
public sealed class FtpTlsSession : IDisposable, IAsyncDisposable
{
    /// <summary>可选的提供器关闭通知回调；返回前只发送 TLS 关闭通知，不释放原 TCP。</summary>
    private readonly Func<CancellationToken, Task>? _shutdown;

    /// <summary>可选的原生会话恢复资源清理函数。</summary>
    private readonly Action? _release;

    /// <summary>防止恢复资源及流被重复释放。</summary>
    private int _disposed;

    /// <summary>创建 TLS 认证结果；恢复元数据只能由实际执行握手的提供器填充。</summary>
    public FtpTlsSession(Stream stream, bool? isResumed = null, bool? isControlSessionReused = null,
        object? resumptionState = null, Func<CancellationToken, Task>? shutdown = null, Action? release = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Stream = stream; IsResumed = isResumed; IsControlSessionReused = isControlSessionReused;
        ResumptionState = resumptionState; _shutdown = shutdown; _release = release;
    }

    /// <summary>已经认证的 TLS 明文视图。</summary>
    public Stream Stream { get; }

    /// <summary>提供器观测到此次握手是否恢复了先前会话；平台无法观测时为空。</summary>
    public bool? IsResumed { get; }

    /// <summary>是否已经验证此次数据握手绑定到传入的指定控制会话；不可推测为 true。</summary>
    public bool? IsControlSessionReused { get; }

    /// <summary>只供同一提供器消费的不透明恢复状态，不应记录或序列化。</summary>
    public object? ResumptionState { get; }

    /// <summary>发送 TLS close_notify，以便随后保留原 TCP 并切回明文。</summary>
    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_shutdown is not null) return _shutdown(cancellationToken);
        if (Stream is SslStream tls) return tls.ShutdownAsync().WaitAsync(cancellationToken);
        throw new NotSupportedException("此 TLS 提供器没有实现保留底层传输的关闭通知。");
    }

    /// <summary>释放流及提供器拥有的恢复资源，可重复调用。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { Stream.Dispose(); } finally { _release?.Invoke(); }
    }

    /// <summary>异步释放流及提供器拥有的恢复资源。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await Stream.DisposeAsync().ConfigureAwait(false); } finally { _release?.Invoke(); }
    }
}

/// <summary>
/// BCL TLS 提供器。操作系统自行决定会话缓存与恢复；SslStream 未公开“导出/指定控制会话”及“握手是否恢复”的跨平台 API，
/// 因此恢复验证字段保持 null。严格复用策略会明确拒绝未知结果，不能将 AllowTlsResume 当作复用成功证明。
/// </summary>
public sealed class FtpPlatformTlsProvider : IFtpTlsProvider
{
    /// <summary>无可变会话状态的共享提供器实例。</summary>
    public static FtpPlatformTlsProvider Instance { get; } = new();

    /// <summary>建立符合调用者主机名与证书策略的 TLS 连接。</summary>
    public async Task<FtpTlsSession> AuthenticateAsync(FtpTlsAuthenticationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var tls = new SslStream(context.Transport, context.LeaveTransportOpen);
        try
        {
            await tls.AuthenticateAsClientAsync(context.AuthenticationOptions, cancellationToken).ConfigureAwait(false);
            return new FtpTlsSession(tls);
        }
        catch { tls.Dispose(); throw; }
    }
}