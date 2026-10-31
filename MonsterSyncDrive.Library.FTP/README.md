<!--
MIT License

Copyright (c) 2025 - 2026 MonsterSyncDrive.Com

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
-->

# MonsterSyncDrive.Library.FTP

面向 .NET 10 的独立 FTP / FTPS 客户端库，使用 `Socket/TcpClient`、`SslStream` 等 BCL API 自行实现协议。运行库没有第三方 NuGet 依赖，也不引用本仓库的业务框架。支持 Windows、Linux、macOS，以及 .NET Native AOT 和裁剪。

独立入口是仓库根目录的 [MonsterSyncDrive.Library.FTP.slnx](../MonsterSyncDrive.Library.FTP.slnx)。客户端、配置、模型和异常统一位于 `MonsterSyncDrive.Library.FTP`，协议编解码器位于 `MonsterSyncDrive.Library.FTP.Protocol`。

## 功能

| 范围 | 支持内容 |
| --- | --- |
| 连接与登录 | DNS / IPv4 / IPv6、匿名 / USER / PASS / ACCT、HOST、REIN 后同 TCP 重新登录/切换用户、120 延迟欢迎、FEAT、UTF-8 协商、NOOP、QUIT |
| 安全 | 普通 FTP、AUTH TLS 显式 FTPS、990 隐式 FTPS、PROT P / C、系统证书验证、吊销检查、证书固定回调、客户端证书 |
| 数据连接 | EPSV / PASV / EPRT / PORT / LPRT / LPSV，扩展模式自动回退，主动模式绑定与 NAT 公布地址 |
| 传输 | STOR / STOU / RETR / APPE、REST 字节偏移/不透明检查点、110 MARK 映射、任意 Stream、本地文件、有界 byte[]、传输进度 |
| 数据流 | OpenReadAsync / OpenWriteAsync，流完成时核对服务器 226 / 250，显式完成与中止 |
| 目录与元数据 | MLSD / MLST、OPTS MLST、UNIX/DOS/EPLF/VMS/OS400/MVS LIST、SIZE、MDTM、MFMT/MFCT、创建时间/所有者/组/权限等强类型 facts |
| 修改 | 创建及逐层创建目录、删除文件、递归删除目录、RNFR / RNTO 移动重命名、SITE CHMOD |
| 摘要 | HASH / OPTS HASH，或 XSHA256 / XSHA512 / XSHA1 / XMD5，支持 SHA-256 / SHA-512 / SHA-1 / MD5 |
| 扩展 | ALLO、SMNT、SITE 单步/多步事务、传输期间 STAT、通用 OPTS、ExecuteAsync 单个最终回复扩展命令 |
| 表示与分帧 | TYPE A/E/I/L、格式 N/T/C、MODE S/B/C/Z、STRU F/R/P、显式记录和 36/64 位逻辑页 API |
| 安全扩展 | AUTH/ADAT 可插入安全机制、BCL GSSAPI/Kerberos/Negotiate/NTLM、MIC/CONF/ENC、631/632/633、CCC、可验证 TLS 提供器 |
| 中止 | 客户端/流级 ABOR 保留控制会话，消费 426+226/225/226，与传输中 STAT 和完成回复协调 |
| 稳定性 | 多行回复、Telnet IAC、连接/命令/数据空闲超时、取消、串行会话、资源限额、失败后关闭不同步连接 |

MFMT/MFCT、CHMOD、HASH、MODE Z 等需要服务器支持。默认使用 TYPE I / MODE S / STRU F，调用者可以显式更换参数；协商成功之后会实际应用对应的文本转换、分帧和压缩。不提供 SFTP（SSH 协议）、FXP 或代理。

## 快速开始

项目引用：

```xml
<ProjectReference Include="../MonsterSyncDrive.Library.FTP/MonsterSyncDrive.Library.FTP.csproj" />
```

使用操作系统默认的证书验证连接显式 FTPS：

```csharp
using MonsterSyncDrive.Library.FTP;

await using var ftp = new FtpClient(new FtpOptions
{
    Host = "ftp.example.com",
    UserName = "user",
    Password = passwordFromConfiguration,
    Encryption = FtpEncryptionMode.ExplicitTls,
    DataConnectionMode = FtpDataConnectionMode.AutoPassive
});
await ftp.ConnectAsync(cancellationToken);

await ftp.CreateDirectoryAsync("/backup/2026", recursive: true,
    cancellationToken: cancellationToken);
await ftp.UploadFileAsync("photo.jpg", "/backup/2026/photo.jpg",
    cancellationToken: cancellationToken);

foreach (var item in await ftp.ListAsync("/backup/2026",
    cancellationToken: cancellationToken))
{
    Console.WriteLine($"{item.Type} {item.Name} {item.Size}");
}

await ftp.DownloadFileAsync("/backup/2026/photo.jpg", "download.jpg",
    cancellationToken: cancellationToken);
await ftp.DisconnectAsync(cancellationToken);
```

普通 FTP 使用 `Encryption = FtpEncryptionMode.None`（默认值），隐式 FTPS 使用 `ImplicitTls`。默认端口为 21，隐式 FTPS 为 990，可通过 `Port` 覆盖。显式 TLS 升级或证书验证失败时不会回退明文登录。数据通道默认加密；只有显式设置 `EncryptDataChannel = false` 才发送 PROT C。

## 流与会话的使用规则

一个实例拥有一个控制连接。普通 API 自动排队；需要并行文件传输时创建多个客户端。打开的数据流独占会话；ABOR 与 STAT 使用独立控制协调器，可以在传输期间执行。

```csharp
await using (var remote = await ftp.OpenReadAsync("/large.bin",
    cancellationToken: cancellationToken))
{
    await using var local = File.Create("large.bin");
    await remote.CopyToAsync(local, cancellationToken);
} // 读到 EOF 时已经检查最终完成回复。

await using (var remote = await ftp.OpenWriteAsync("/upload.bin",
    cancellationToken: cancellationToken))
{
    await source.CopyToAsync(remote, cancellationToken);
    await remote.CompleteAsync(); // 发送 EOF，并等待 226 / 250；之后可执行其他命令。
}
```

- 下载流提前释放会关闭会话，不会把残留的传输完成回复留给下一条命令。
- 上传流必须完成或释放，最终服务器错误会从 `CompleteAsync` / `DisposeAsync` 传播；应使用 `await using`。
- 客户端 `AbortAsync` 和数据流 `AbortAsync` 执行真正的 ABOR，正确消费双回复并保留会话。提前释放未读完的下载流仍会关闭会话；若要保留会话，应显式中止。
- ABOR/STAT 用 NOOP 回复屏障区分在途传输完成和命令完成，不依赖猜测性的短等待。无法确认同步时关闭控制连接。
- 数据流不支持 Length、Position、Seek 和 SetLength；打开时可以指定断点。
- 调用者提供给 `DownloadAsync` / `UploadAsync` 的流保持打开；这些方法不替调用者定位源流或目标流。
- 客户端本身可以在任意线程释放，以中断等待和传输；不需要等待遗留流归还锁。

## 断点续传、追加与进度

```csharp
var progress = new Progress<FtpTransferProgress>(p =>
    Console.WriteLine($"{p.BytesTransferred} bytes, {p.Percentage}%, {p.BytesPerSecond:F0} B/s"));

// 下载：从现有本地文件末尾续传，先查询远程 SIZE。
await ftp.DownloadFileAsync("/large.bin", "large.bin", resume: true,
    progress: progress, cancellationToken: cancellationToken);

// 上传：要求远程文件已存在且 SIZE 可查询；库自动定位本地源文件。
await ftp.UploadFileAsync("large.bin", "/large.bin", FtpUploadMode.Resume,
    progress, cancellationToken);

// APPE：把整个源流追加到远程文件，不能与 REST 混用。
await ftp.UploadBytesAsync("/log.bin", new byte[] { 1, 2, 3 },
    append: true, cancellationToken: cancellationToken);
```

使用低层流复制接口时，`restartOffset` 只作用于远程文件。上传源流或下载目标流的本地位置由调用者设置。字节偏移及本地文件长度续传仅允许 TYPE I / L 8、MODE S、STRU F。其他模式使用 `OpenReadAsync(path, string marker)`、`OpenWriteAsync(path, string marker)` 或 `DownloadFromMarkerAsync` / `UploadFromMarkerAsync`，传入服务器提供的不透明标记并自行定位本地检查点。REST 紧邻 RETR/STOR 发送，中间不插入其他命令。

MODE B/C 的数据内标记通过 `RestartMarkerReceived` 回调和流的 `LastRestartMarker` 提供；服务器的 `110 MARK yyyy = mmmm` 映射保存在 `RestartCheckpoints`。不能把服务器的标记擅自转换为本地字节偏移。

续传检查大小，无法证明已存在的前缀相同。重要文件可比较 `GetChecksumAsync` 的结果。普通覆盖下载打开本地文件时即截断旧内容；失败可能保留部分文件。需要原子替换时，先下载到调用者选择的临时文件，成功后自行替换目标文件。库不自动重试写操作。

`BytesTransferred` 是本次复制量，不含断点；`RestartOffset` 是已有量；`TotalBytes` 未知时百分比为空。上传源流可定位时提供总大小，普通 Stream 下载不强制依赖 SIZE。进度处理器若同步抛出异常，会中断操作。

## 路径、列表和递归

远程路径使用正斜杠，原样发送，不执行 URL 解码或本地 `Path.Combine`。空格、Unicode、百分号、分号保留。所有命令参数禁止 ASCII 控制字符，避免 CRLF 注入。

优先 MLSD/MLST，facts 字典不区分大小写并保留未知字段。强类型属性包括修改/创建时间、所有者/组、UID/GID、链接数、唯一标识、访问权限、媒体类型及字符集等；`SetMachineListingFactsAsync` 保存服务器实际接受的子集，也支持仅返回名称的空 facts。

传统 LIST 支持常见 UNIX/ACL、DOS/IIS、EPLF、VMS 续行、OS400 对象/成员和 MVS 数据集/成员格式；没有可信时区的时间保留为 Unspecified。VMS 块占用、MVS 磁道/记录数保留为 facts，不冒充字节大小。未知格式、重复名称和不安全子项拒绝猜测。LIST 方言与原生文件系统路径并无统一语法，不能据此宣称兼容所有主机文件系统；非标准格式可读取原始列表自行解释。

只有服务器明确回复命令不支持时才降级；550 权限错误不会触发 LIST 或明文降级。`GetItemAsync` 的 MLST 550 返回空，`FileExistsAsync` / `DirectoryExistsAsync` 相应返回 false。FTP 的 550 可能表示不存在或没有权限，不能据此证明资源不存在。SIZE 和文件传输直接保留错误回复。

递归枚举和删除不跟随符号链接；拒绝未知类型、穿越子项、超深目录和超量条目。递归删除先验证完整计划再修改文件树，禁止根目录与含点目录段的目标。FTP 没有文件系统事务，执行中失败或其他会话同时修改目录时，可能已完成部分删除。

## 网络与服务器配置

- 默认优先 EPSV。IPv4 服务器明确不支持时才使用 PASV，IPv6 不回退 IPv4 命令。
- PASV 默认忽略回复中的 IP，连接控制通道实际对端；只有显式设置 `TrustPassiveAddress` 才信任服务器公布地址。
- 主动模式只接受控制连接对端 IP 的入站连接。可设置 `ActiveBindAddress`、`ActivePort` 和 `ActiveAdvertisedAddress` 配合 NAT 端口映射。
- 控制与数据通道都校验证书。私有 CA 可使用 `CertificateValidation` 实现受限信任或固定证书；不要使用无条件通过的验证回调。
- `TlsSessionReuse` 可选择 Disabled、Allow 或 RequireControlSessionReuse；`ControlTlsSessionResumed`、`DataTlsSessionResumed`、`DataTlsControlSessionReused` 为可空观测值，null 表示无法观测。默认 SslStream 没有公开的跨平台会话导出/指定/验证 API，因此不能保证严格复用。严格策略遇到未知状态会明确抛 NotSupportedException；具备真实会话 API 的宿主可实现 `IFtpTlsProvider`。提供器不能仅依据相同主机名或证书就谎称复用成功。
- 默认严格 UTF-8。旧服务器可提供兼容 ASCII 的编码；GBK 等编码需要应用注册微软的 CodePages provider。编码错误不会静默替换文件名。

## 扩展会话与结构化传输

REIN 保留 TCP，但清除身份、能力、HOST、选项、检查点及传输参数。TLS 会话按 RFC 4217 在受保护的 REIN 回复之后关闭，后续登录重新 AUTH TLS。可在同一 TCP 上选择其他虚拟主机及用户：

```csharp
await ftp.ReinitializeAsync(cancellationToken);
await ftp.SelectVirtualHostAsync("tenant.example.com", cancellationToken);
await ftp.LoginAsync("another-user", anotherPassword, cancellationToken: cancellationToken);
```

初次登录前 HOST 可通过 `FtpOptions.VirtualHost` 配置。`Host` 是 TCP 地址，`TlsTargetHost` 可独立指定证书/SNI 名称；没有覆盖时使用所选虚拟主机名。

```csharp
// 唯一名称由服务器返回，方法成功时已经检查完成回复。
var stored = await ftp.UploadUniqueFileAsync("report.csv", "report",
    cancellationToken: cancellationToken);
Console.WriteLine(stored.RemotePath);

// TYPE/MODE/STRU 与实际编解码绑定；逐项协商失败不会伪装成所有参数都已更改。
await ftp.SetTransferSettingsAsync(new FtpTransferSettings
{
    DataType = FtpDataType.Image,
    Mode = FtpTransmissionMode.Deflate,
    Structure = FtpFileStructure.File,
    MaxDecodedBytes = 128 * 1024 * 1024
}, cancellationToken);
```

TYPE A 的应用端是七位 ASCII，LF/CRLF 规范化为 NVT CRLF，独立 CR 编码为 CR NUL；TYPE E 的应用端按 Latin1 字节映射至 EBCDIC CP037，NL 正确转换。N/T/C 都能协商；T/ASA 格式控制符由应用/打印消费者解释，库保留其内容，不进行打印排版。文件数据编码与 UTF-8 文件名编码相互独立。

TYPE L 的普通 Stream 暴露调用者明确提供的紧密打包位流，不猜测本机 36 位字存储形式。STRU R 的普通字节流用 LF 表示记录边界；`UploadRecordsAsync` / `DownloadRecordsAsync` 则保留空记录及记录内部任意 LF/0xff。STRU P 必须用 `UploadPagesAsync` / `DownloadPagesAsync`，`FtpPage` 保留页索引、页类型、扩展头和逻辑字节值，支持 3–64 位逻辑页字段。普通页/访问控制页/描述页/结束页均有实际帧处理与预算校验。

MODE B 使用三字节大端块头，MODE C 是 RFC 959 的字节串/重复/填充编码，MODE Z 是 zlib/DEFLATE；三者不能混淆。先完成表示和分帧/压缩，再发送 TLS 关闭通知。中止路径不会补发表示成功的压缩尾帧或 EOF。记录/页内存下载有独立的条目数与总内存预算。

`AllocateAsync` 支持 ALLO 及 R 最大记录参数；`MountAsync` 支持 SMNT。`SiteAsync` 处理单步操作，需要中间回复的 SITE 操作使用显式多步事务：

```csharp
await ftp.ExecuteSiteSequenceAsync(new[]
{
    new FtpSiteCommand { Arguments = "CPFR /source", RequiresContinuation = true },
    new FtpSiteCommand { Arguments = "CPTO /destination" }
}, cancellationToken);
await ftp.SetMachineListingFactsAsync(new[] { "type", "size", "modify", "create", "unix.owner" }, cancellationToken);
await ftp.SetCreatedTimeAsync("/destination", DateTimeOffset.UtcNow, cancellationToken);
```

`SetCreatedTimeAsync` 返回服务器实际保存的创建时间；文件系统可能降低精度。`SetOptionsAsync` 可发送服务器支持的选项，MLST 会更新字段缓存；未知服务器专用选项的语义由调用者遵循服务器文档。它不能代替 TYPE/MODE/STRU 的状态设置。

## AUTH/ADAT 与受保护控制回复

`FtpSecurityOptions.MechanismFactory` 为每次连接/REIN 后认证创建独立机制。库执行 AUTH/ADAT 的 challenge-response，限制交换次数、令牌大小，默认用 ENC 保护后续凭据和命令；631/632/633 多行片段逐个认证后再重组，拒绝篡改、保护降级、嵌套回复和额外拼接回复。

```csharp
var options = new FtpOptions
{
    Host = "ftp.example.com",
    UserName = "user",
    Password = passwordFromConfiguration,
    Security = new FtpSecurityOptions
    {
        MechanismFactory = () => new FtpNegotiateSecurityMechanism("ftp/ftp.example.com", package: "Kerberos"),
        CommandProtection = FtpCommandProtection.Privacy
    }
};
```

内置机制调用 BCL `NegotiateAuthentication`，需要目标操作系统支持及实际的凭据/SPN/Kerberos 域环境。库没有内置旧版 Kerberos V4 或所有厂商算法；可用 `IFtpSecurityMechanism` 接入其他真实机制。RFC 2228 的 GSSAPI 定义 MIC/ENC 和 631/632，不定义 CONF/633，因此内置 GSSAPI 正确拒绝该组合；支持 CONF 的其他机制可通过相同管线使用。放宽完整性要求前必须成功执行 CCC。

`ClearCommandChannelAsync` 对 TLS 执行 close_notify 双向结束后恢复原始明文流；数据连接仍遵循此前 PROT P。对通用安全关联则放宽控制命令完整性要求，保留安全关联供明确选择的后续保护使用。

确定性消息保护测试使用测试服务器专属的 HMAC/AES-GCM 机制，未用伪令牌冒充生产 GSSAPI，也未宣称已在真实 Kerberos 域验证互通。TLS 严格会话复用的可验证提供器要求见前文，不把 null 当成已恢复。

## 超时、取消、错误与限额

`ConnectTimeout` 覆盖一次 TCP 连接或 TLS 握手；`CommandTimeout` 覆盖一次命令及完整回复；`DataTimeout` 限制单次读取、写入和主动连接等待。长时间持续传输不受固定总时长限制。可显式使用 `Timeout.InfiniteTimeSpan` 关闭某类超时。

| 情况 | 行为 |
| --- | --- |
| 排队时取消 | OperationCanceledException，不影响正在运行的会话 |
| 普通调用者取消或网络故障 | 关闭会话；重新使用前显式 ConnectAsync / ReconnectAsync |
| 显式 ABOR 成功同步 | 保留会话；被中止操作报告 FtpTransferAbortedException |
| 阶段超时 | TimeoutException，关闭会话 |
| 格式错误、资源限制、传输失败 | 关闭可能不同步的会话 |
| 已完整读取的普通命令错误 | FtpCommandException，通常可继续使用会话 |
| 426 或其他传输最终错误 | 即使已复制所有字节也失败，关闭会话 |
| 421 会话关闭 | 立即关闭连接，不继续复用 |

`IsConnected` 仅表示最近已知状态，可用 `NoopAsync` 实际探测。异常的 `Command` 只含动词，消息不包含密码；服务器原始 `Reply` 仍可能包含敏感文本，由应用决定是否记录。

默认复制缓冲区 64 KiB；控制/列表单行 16 KiB；完整控制回复 1 MiB / 1024 行；目录数据 16 MiB / 100000 条；递归深度 64 / 总条目 1000000。`DownloadBytesAsync` 默认内存上限 64 MiB，可单独指定。大型文件优先使用流。

## 构建和测试

在仓库根目录构建独立解决方案：

```text
dotnet build MonsterSyncDrive.Library.FTP.slnx -c Release
```

以下测试命令在 MonsterSyncDrive.App 仓库中执行，测试工程仍名为 `Test.MonsterSyncDrive.FTP`；验证本独立库时，需要将测试项目的库引用和 using 同步为本库及上述命名空间。测试使用微软 MSTest + Microsoft.Testing.Platform。测试目录内的 `global.json` 只为本测试入口选择 .NET 10 测试运行器，避免改变仓库其他工程的测试配置。**从测试目录运行**：

```text
cd Test.MonsterSyncDrive.FTP
dotnet test --project Test.MonsterSyncDrive.FTP.csproj -c Release
```

也可以从仓库根目录直接运行测试可执行项目：

```text
dotnet run --project Test.MonsterSyncDrive.FTP/Test.MonsterSyncDrive.FTP.csproj -c Release
```

测试创建本地临时 FTP/FTPS 服务器，覆盖主动/被动及历史端点、IPv6、真实 TLS、AUTH 消息保护、REIN/CCC、ABOR/STAT 并发、STOU、强类型 facts、TYPE/MODE/STRU 网络向量、MODE Z、REST 检查点、失败回复与资源限额。不依赖外部 FTP 服务。独立向量服务器不会复用被测客户端编解码器生成预期字节，避免编码器与解码器相同错误互相掩盖。库和测试的直接、传递 NuGet 依赖均仅来自微软。

## Native AOT

库开启 `IsAotCompatible`、裁剪和 AOT 分析，不使用运行时生成代码、反射序列化或动态加载。应用项目需要自己设置 `PublishAot`。实际发布需要目标平台的本机工具链，参见 [.NET Native AOT 官方说明](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)。

在 MonsterSyncDrive.App 仓库根目录发布并运行独立 AOT 验证入口（验证本库时须先同步项目引用和 using）：

```text
dotnet publish Test.MonsterSyncDrive.FTP/Test.MonsterSyncDrive.FTP.csproj -c Release -r win-x64 -p:AotSmoke=true --artifacts-path artifacts/ftp-aot-build -o artifacts/ftp-aot/win-x64
artifacts/ftp-aot/win-x64/Test.MonsterSyncDrive.FTP.exe
```

Linux/macOS 在对应系统上改用 `linux-x64`、`linux-arm64`、`osx-x64` 或 `osx-arm64`，运行不带 .exe 的产物。独立产物目录避免覆盖普通测试的依赖还原文件。

AOT 入口不引用测试框架，实际执行普通被动/主动 FTP、显式/隐式 FTPS、TYPE A、MODE B/Z、STOU、REIN/HOST/OPTS、AUTH 消息保护和 CCC 场景，同时把整个客户端程序集加入裁剪分析根，并将裁剪/AOT 警告作为错误。已在 Windows x64 完成原生发布及执行；Linux/macOS 由随附 CI 在对应系统执行，不把未运行的平台宣称为已验证。

## 协议与许可

主要依据 [RFC 959](https://www.rfc-editor.org/rfc/rfc959.html)、[RFC 2428](https://www.rfc-editor.org/rfc/rfc2428.html)、[RFC 3659](https://www.rfc-editor.org/rfc/rfc3659.html)、[RFC 4217](https://www.rfc-editor.org/rfc/rfc4217.html)。服务器可选扩展按实际回复判断。

源代码、测试及本文档采用 MIT License；完整许可见文件头和仓库 [LICENSE](../LICENSE)。
