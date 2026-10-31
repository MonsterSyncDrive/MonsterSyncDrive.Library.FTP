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

/// <summary>显式的 TYPE、MODE、STRU 协商及当前会话传输参数；修改参数和传输共用同一会话锁。</summary>
public sealed partial class FtpClient
{
    /// <summary>服务器当前已经接受的传输参数；连接和 REIN 的协议默认值是 ASCII、流模式、文件结构。</summary>
    private FtpTransferSettings _transferSettings = new() { DataType = FtpDataType.Ascii };

    /// <summary>数据通道是否应进行 TLS 握手，独立于控制通道是否通过 CCC 恢复明文。</summary>
    private bool _dataChannelTls;

    /// <summary>最近一次传输的预备回复，保留 STOU 服务器选择的唯一文件名。</summary>
    private FtpReply? _lastDataPreliminaryReply;

    /// <summary>最近一次传输的服务器 110 重启映射，控制读者和应用查询可以在不同线程执行。</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<FtpRestartCheckpoint> _restartCheckpoints = new();

    /// <summary>最近一次传输收到的服务器重启映射快照。</summary>
    public IReadOnlyList<FtpRestartCheckpoint> RestartCheckpoints => Array.AsReadOnly(_restartCheckpoints.ToArray());

    /// <summary>处理 110 MARK yyyy = mmmm，严格验证两端标记，并限制检查点总量。</summary>
    private void ProcessRestartMarkerReply(FtpReply reply)
    {
        string text = reply.Message;
        int separator = text.IndexOf(" = ", StringComparison.Ordinal);
        if (!text.StartsWith("MARK ", StringComparison.OrdinalIgnoreCase) || separator <= 5 || separator + 3 >= text.Length)
            throw new FtpProtocolException("110 回复缺少 MARK 本地标记 = 服务器标记。");
        string local = text[5..separator];
        string remote = text[(separator + 3)..];
        ValidateOpaqueRestartMarker(local);
        ValidateOpaqueRestartMarker(remote);
        if (_restartCheckpoints.Count >= _options.MaxReplyLines)
            throw new FtpProtocolException("服务器重启映射超过回复数量限制。");
        FtpRestartCheckpoint checkpoint = new()
        {
            UserMarker = local,
            ServerMarker = remote,
            Reply = reply
        };
        _restartCheckpoints.Enqueue(checkpoint);
    }

    /// <summary>当前服务器已经接受的传输参数快照。属性更改通过 SetTransferSettingsAsync 完成。</summary>
    public FtpTransferSettings TransferSettings => _transferSettings.Snapshot();

    /// <summary>设置完整的传输参数；按 TYPE、MODE、STRU 依次协商，每项成功后才更新对应本地状态。</summary>
    public Task SetTransferSettingsAsync(FtpTransferSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = settings.Snapshot();
        return RunAsync(token => ApplyTransferSettingsCoreAsync(snapshot, token), cancellationToken);
    }

    /// <summary>设置数据表示。文本使用 ASCII/EBCDIC 和指定格式控制；Local 指定逻辑字节位数。</summary>
    public Task SetDataTypeAsync(FtpDataType dataType, FtpTextFormat textFormat = FtpTextFormat.NonPrint,
        int localByteSize = 8, CancellationToken cancellationToken = default)
    {
        return RunAsync(token => ApplyTransferSettingsCoreAsync(_transferSettings with
        {
            DataType = dataType,
            TextFormat = textFormat,
            LocalByteSize = localByteSize
        }, token), cancellationToken);
    }

    /// <summary>选择流、块、RFC 压缩或 Deflate MODE Z，实际数据编码和协商值一起改变。</summary>
    public Task SetTransmissionModeAsync(FtpTransmissionMode mode, CancellationToken cancellationToken = default)
    {
        return RunAsync(token => ApplyTransferSettingsCoreAsync(_transferSettings with { Mode = mode }, token), cancellationToken);
    }

    /// <summary>选择文件、记录或页结构。页结构使用专门页 API，避免丢失页索引及访问控制信息。</summary>
    public Task SetFileStructureAsync(FtpFileStructure structure, CancellationToken cancellationToken = default)
    {
        return RunAsync(token => ApplyTransferSettingsCoreAsync(_transferSettings with { Structure = structure }, token), cancellationToken);
    }

    /// <summary>执行参数差异协商；失败时保留已被服务器接受的部分状态，不伪装为一次原子设置。</summary>
    private async Task ApplyTransferSettingsCoreAsync(FtpTransferSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        string typeArgument = settings.GetTypeArgument();
        if (_transferSettings.GetTypeArgument() != typeArgument)
        {
            Expect("TYPE", await CommandAsync("TYPE", typeArgument, cancellationToken).ConfigureAwait(false), 200);
            _transferSettings = _transferSettings with
            {
                DataType = settings.DataType,
                TextFormat = settings.TextFormat,
                LocalByteSize = settings.LocalByteSize
            };
        }
        if (_transferSettings.Mode != settings.Mode)
        {
            Expect("MODE", await CommandAsync("MODE", settings.GetModeArgument(), cancellationToken).ConfigureAwait(false), 200);
            _transferSettings = _transferSettings with
            {
                Mode = settings.Mode
            };
        }
        if (_transferSettings.Structure != settings.Structure)
        {
            Expect("STRU", await CommandAsync("STRU", settings.GetStructureArgument(), cancellationToken).ConfigureAwait(false), 200);
            _transferSettings = _transferSettings with
            {
                Structure = settings.Structure
            };
        }
        _transferSettings = settings.Snapshot();
    }

    /// <summary>只有 RFC 3659 的字节流组合可以把本地文件长度直接当成 REST 偏移。</summary>
    private void ValidateByteRestart(long offset)
    {
        if (offset == 0)
            return;
        if (_transferSettings.Mode != FtpTransmissionMode.Stream || _transferSettings.Structure != FtpFileStructure.File ||
            !(_transferSettings.DataType == FtpDataType.Image ||
              (_transferSettings.DataType == FtpDataType.Local && _transferSettings.LocalByteSize == 8)))
            throw new NotSupportedException("字节偏移续传要求 TYPE I/TYPE L 8、MODE S、STRU F；其他模式请使用服务器提供的 opaque 重启标记。");
    }

    /// <summary>在复用的 TCP 会话上执行 REIN 后恢复 RFC 默认状态，包括清除 TLS 会话。</summary>
    private async Task ResetSessionAfterReinitializeAsync(CancellationToken cancellationToken)
    {
        if (_controlTlsSession is not null)
            await DowngradeControlTlsCoreAsync(cancellationToken).ConfigureAwait(false);
        ResetSecurityState(keepTlsTransport: true);
        Volatile.Write(ref _connected, 0);
        _dataChannelTls = false;
        _transferSettings = new FtpTransferSettings { DataType = FtpDataType.Ascii };
        Features = Array.Empty<string>();
        _lastDataPreliminaryReply = null;
        _restartCheckpoints.Clear();
        _pendingSequence = false;
        ResetSessionExtensionState();
        ResetActiveTransferState();
    }
}
