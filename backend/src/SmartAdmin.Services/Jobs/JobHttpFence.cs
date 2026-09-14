using System.Net;
using System.Net.Sockets;
using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// HTTP 任务的 SSRF 围栏。IP/CIDR/URL 判定与连接期复检已抽到 <see cref="HttpFence"/>(AI 网关共用),
/// 本类只保留原签名转发 + 任务专属的请求头校验,消费者(JobService/JobHttpClient)不受影响。
/// 默认封云元数据段(169.254.0.0/16、fd00:ec2::/32)、链路本地与回环(127.0.0.0/8、::1/128),
/// <b>不封内网</b>:调度器打内网服务是主用途,而打宿主自己的管理端口不是。
/// </summary>
public static class JobHttpFence
{
    /// <summary>URL 静态校验;不过关抛 <see cref="ErrorCode.JobHttpUrlBlocked"/>(47009)。</summary>
    public static void ValidateUrl(string url, AdminJobsHttpOptions http) =>
        HttpFence.ValidateUrl(url, ToFenceOptions(http), ErrorCode.JobHttpUrlBlocked);

    /// <summary>
    /// 校验一对请求头。<b>拦 CRLF 注入</b>:<c>TryAddWithoutValidation</c> 对含 CR/LF 的值原样上线路,
    /// 内部人能借此在同一连接上走私第二个请求(方法/路径/Host 全自选),ValidateUrl 与执行记录都看不见它。
    /// 入库与执行两处都调本方法;不合规抛 <see cref="ErrorCode.JobPropsInvalid"/>。任务专属校验,不进 <see cref="HttpFence"/>
    /// (AI 适配器只发固定的鉴权头,不接受用户自定义请求头)。
    /// </summary>
    public static void ValidateHeader(string name, string? value)
    {
        // 名字必须是 HTTP token(RFC 9110):可见 ASCII 且不含分隔符
        const string separators = "()<>@,;:\\\"/[]?={} \t";
        var nameOk = !string.IsNullOrEmpty(name)
            && name.All(c => c > 0x20 && c < 0x7F && !separators.Contains(c));
        AdminException.ThrowIf(!nameOk, ErrorCode.JobPropsInvalid,
            new Dictionary<string, object?> { ["key"] = "headers" });
        // 值不许出现任何控制字符(制表符除外)——CR/LF/NUL 都在此拦下
        var valueOk = value is null || value.All(c => c == '\t' || (c >= 0x20 && c != 0x7F));
        AdminException.ThrowIf(!valueOk, ErrorCode.JobPropsInvalid,
            new Dictionary<string, object?> { ["key"] = "headers" });
    }

    /// <summary>IP 是否命中 CIDR 黑名单;转发 <see cref="HttpFence.IsBlocked"/>。</summary>
    public static bool IsBlocked(IPAddress ip, string[] cidrs) => HttpFence.IsBlocked(ip, cidrs);

    /// <summary>解析一条 CIDR;转发 <see cref="HttpFence.TryParseCidr"/>。</summary>
    public static bool TryParseCidr(string cidr, out IPAddress? network, out int bits) =>
        HttpFence.TryParseCidr(cidr, out network, out bits);

    /// <summary>带围栏的 <see cref="SocketsHttpHandler"/>;转发 <see cref="HttpFence.CreateHandler"/>(任务侧不配代理,始终走 ConnectCallback 分支)。</summary>
    public static SocketsHttpHandler CreateHandler(AdminJobsHttpOptions http) => HttpFence.CreateHandler(ToFenceOptions(http));

    private static HttpFenceOptions ToFenceOptions(AdminJobsHttpOptions http) =>
        new() { AllowedHosts = http.AllowedHosts, BlockedCidrs = http.BlockedCidrs };
}
