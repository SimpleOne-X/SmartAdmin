using System.Net;
using System.Net.Sockets;
using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// 出站 HTTP 请求的 SSRF 围栏,定时任务(<see cref="JobHttpFence"/> 转发)与 AI 网关共用。
/// 规则与原 <c>JobHttpFence</c> 一致:仅 http/https、主机白名单、CIDR 黑名单;解析后的 IP 在
/// <see cref="CreateHandler"/> 的 ConnectCallback 里复检,防 DNS rebinding(校验时解析成公网、执行时解析成内网的把戏)。
/// 抛出的 <see cref="ErrorCode"/> 由调用方传入——不同消费者(Job/AI)各有自己的错误码语义。
/// </summary>
public static class HttpFence
{
    /// <summary>URL 静态校验;不过关抛 <paramref name="blockedCode"/>。</summary>
    public static void ValidateUrl(string url, HttpFenceOptions options, ErrorCode blockedCode)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new AdminException(blockedCode, new Dictionary<string, object?> { ["url"] = url },
                $"URL 非法或协议不受支持(仅 http/https):{url}");
        if (options.AllowedHosts is { Length: > 0 } allow
            && !allow.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase)))
            throw new AdminException(blockedCode, new Dictionary<string, object?> { ["url"] = url },
                $"主机不在白名单:{uri.Host}");
        if (IPAddress.TryParse(uri.Host, out var literal) && IsBlocked(literal, options.BlockedCidrs))
            throw new AdminException(blockedCode, new Dictionary<string, object?> { ["url"] = url },
                $"目标地址命中围栏黑名单:{uri.Host}");
        // 域名的解析后复检在 ConnectCallback——此处只拦字面 IP,不做"校验时解析"(那正是 rebinding 绕过面)
    }

    /// <summary>IP 是否命中 CIDR 黑名单(IPv4 映射 IPv6 先折回 IPv4)。</summary>
    public static bool IsBlocked(IPAddress ip, string[] cidrs)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return cidrs.Any(c => Matches(ip, c));
    }

    /// <summary>
    /// 解析一条 CIDR;不合法返回 false。无斜杠按单地址处理(<c>/32</c> / <c>/128</c>)——
    /// 「169.254.169.254」这种直觉写法能用,不至于手抖一个条目就让整条黑名单静默变空集。
    /// </summary>
    public static bool TryParseCidr(string cidr, out IPAddress? network, out int bits)
    {
        network = null;
        bits = 0;
        var text = cidr.Trim();
        var slash = text.IndexOf('/');
        var addressPart = slash < 0 ? text : text[..slash];
        if (!IPAddress.TryParse(addressPart.Trim(), out network)) return false;
        var maxBits = network.GetAddressBytes().Length * 8;
        if (slash < 0)
        {
            bits = maxBits;
            return true;
        }
        if (!int.TryParse(text[(slash + 1)..].Trim(), out bits) || bits < 0 || bits > maxBits) return false;
        return true;
    }

    private static bool Matches(IPAddress ip, string cidr)
    {
        if (!TryParseCidr(cidr, out var net, out var bits) || net is null) return false;
        if (net.AddressFamily != ip.AddressFamily) return false;
        var ipBytes = ip.GetAddressBytes();
        var netBytes = net.GetAddressBytes();
        var fullBytes = bits / 8;
        var remBits = bits % 8;
        for (var i = 0; i < fullBytes; i++)
            if (ipBytes[i] != netBytes[i]) return false;
        if (remBits > 0)
        {
            var mask = 0xFF << (8 - remBits) & 0xFF;
            if ((ipBytes[fullBytes] & mask) != (netBytes[fullBytes] & mask)) return false;
        }
        return true;
    }

    /// <summary>
    /// 带围栏的 <see cref="SocketsHttpHandler"/>。<b>无代理时</b>(多数部署):禁跟随重定向、禁用系统代理,
    /// 连接期对解析后的每个 IP 复检黑名单,全部命中即拒连;连接池 5 分钟轮换(长命 HttpClient 的 DNS 陈旧问题
    /// 由此解决)。<b>配置了 <see cref="HttpFenceOptions.Proxy"/> 时</b>:改走该代理,不挂 ConnectCallback——
    /// ConnectCallback 只看得见代理的 IP,真实目标由代理去解析去连,IP 围栏对代理链天然失效,这是操作者的主动选择、
    /// 风险自担(与不挂代理时"为什么必须禁代理"的理由互为镜像,见 <see cref="JobHttpFence"/> 原版文档)。
    /// </summary>
    public static SocketsHttpHandler CreateHandler(HttpFenceOptions options)
    {
        if (!string.IsNullOrEmpty(options.Proxy))
            return new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = true,
                Proxy = new WebProxy(options.Proxy),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            Proxy = null,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
                var allowed = addresses.Where(a => !IsBlocked(a, options.BlockedCidrs)).ToArray();
                if (allowed.Length == 0)
                    throw new HttpRequestException(
                        $"目标 {context.DnsEndPoint.Host} 解析后的地址全部命中围栏黑名单(DNS rebinding 防护)");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }
}
