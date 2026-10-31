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

using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace MonsterSyncDrive.Library.FTP.Protocol;

/// <summary>
/// RFC 1639 长地址端点的严格编解码器。历史命令用 IP 版本值 4/6，不能混用 EPRT 的地址族值 1/2。
/// 这里只接受本库能够建立的 IPv4/IPv6 TCP 端点；不把未知地址族或超长端口截断成可连接地址。
/// </summary>
internal static class FtpLongEndpointCodec
{
    /// <summary>格式化 LPRT 参数；IPv4 映射 IPv6 会规范化为四字节地址，TCP 端口按网络字节序输出。</summary>
    /// <param name="endpoint">非零 TCP 数据端口及服务器可访问的公布地址。</param>
    /// <returns>af,hal,h1,...,pal,p1,p2 格式的参数，不含 LPRT 动词。</returns>
    internal static string FormatActive(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(endpoint), "LPRT 数据端口必须非零。");
        IPAddress address = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
        ValidateUnicast(address);
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            throw new ArgumentException("LPRT 不携带 IPv6 scope ID；请公布不含接口作用域的地址。", nameof(endpoint));
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new ArgumentException("LPRT 只支持 IPv4 或 IPv6 TCP 端点。", nameof(endpoint));
        byte[] octets = address.GetAddressBytes();
        var values = new List<string>(octets.Length + 5)
        {
            address.AddressFamily == AddressFamily.InterNetwork ? "4" : "6",
            octets.Length.ToString(CultureInfo.InvariantCulture)
        };
        values.AddRange(octets.Select(static value => value.ToString(CultureInfo.InvariantCulture)));
        values.Add("2");
        values.Add((endpoint.Port >> 8).ToString(CultureInfo.InvariantCulture));
        values.Add((endpoint.Port & 255).ToString(CultureInfo.InvariantCulture));
        return string.Join(',', values);
    }

    /// <summary>
    /// 解析 LPSV 228 回复中的括号端点。返回的是服务器公布地址；调用者仍须应用 TrustPassiveAddress
    /// 策略及地址族校验，默认不能让服务器把被动连接重定向到其他主机。
    /// </summary>
    /// <param name="replyMessage">回复消息部分，允许包含前后的自然语言说明。</param>
    /// <returns>严格验证的 IPv4/IPv6 TCP 端点。</returns>
    internal static IPEndPoint ParsePassive(string replyMessage)
    {
        ArgumentNullException.ThrowIfNull(replyMessage);
        int start = replyMessage.LastIndexOf('(');
        int end = start < 0 ? -1 : replyMessage.IndexOf(')', start + 1);
        return start < 0 || end < 0
            ? throw new FtpProtocolException("LPSV 回复缺少括号端点。")
            : ParseAddress(replyMessage.AsSpan(start + 1, end - start - 1).ToString());
    }

    /// <summary>解析 LPRT 参数或 LPSV 括号中的长地址，用于独立测试和测试服务器的相反方向。</summary>
    /// <param name="value">不带括号的完整长地址。</param>
    /// <returns>长度与地址族都已确认的 TCP 端点。</returns>
    internal static IPEndPoint ParseAddress(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // IPv6 是最长的受支持形式：af + hal + 16 个地址字节 + pal + 2 个端口字节。
        // 在分配字段数组之前限制输入长度，防止把畸形端点误当成不受限的整数序列。
        if (value.Length > 128)
            throw new FtpProtocolException("长地址端点的文本长度无效。");
        string[] fields = value.Split(',');
        if (fields.Length is < 9 or > 21)
            throw new FtpProtocolException("长地址端点的字段数或文本长度无效。");
        byte[] bytes = new byte[fields.Length];
        for (int index = 0; index < fields.Length; index++)
        {
            if (!byte.TryParse(fields[index].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out bytes[index]))
                throw new FtpProtocolException("长地址端点必须由 0 至 255 的十进制八位整数组成。");
        }
        int addressLength = bytes[0] switch
        {
            4 => 4,
            6 => 16,
            _ => throw new FtpProtocolException("长地址端点使用了不支持的地址族。")
        };
        if (bytes[1] != addressLength || bytes.Length != addressLength + 5 || bytes[addressLength + 2] != 2)
            throw new FtpProtocolException("长地址端点的地址长度或 TCP 端口长度不匹配。");
        int port = (bytes[^2] << 8) | bytes[^1];
        if (port == 0)
            throw new FtpProtocolException("长地址端点不能使用零端口。");
        var address = new IPAddress(bytes.AsSpan(2, addressLength));
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        ValidateUnicast(address);
        return new IPEndPoint(address, port);
    }

    /// <summary>拒绝不可能成为独立 TCP 服务器对端的未指定、多播和广播地址。</summary>
    private static void ValidateUnicast(IPAddress address)
    {
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast) || address.IsIPv6Multicast ||
            (address.AddressFamily == AddressFamily.InterNetwork && (address.GetAddressBytes()[0] & 240) == 224))
            throw new FtpProtocolException("长地址端点必须是可连接的单播地址。");
    }
}