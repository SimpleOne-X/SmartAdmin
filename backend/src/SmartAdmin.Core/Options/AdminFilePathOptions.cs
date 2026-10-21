namespace SmartAdmin.Core;

/// <summary>
/// 文件路径配置(对应 <c>SmartAdmin:FilePath</c> 节)。与 <see cref="AdminUploadOptions"/> 互相独立。
/// </summary>
public class AdminFilePathOptions
{
    /// <summary>
    /// 文件服务器根路径的白名单(必须是绝对路径,如 <c>\\nas01\files</c>、<c>/mnt/files</c>)。
    /// <para>配了它就是安全边界:界面上的管理员只能在这些根目录之内选路径,填不进程序目录、静态站点目录,也绕不回本机;
    /// 新增一个共享要改配置并重启。</para>
    /// <para><b>空(默认)= 根路径直接在文件服务器页面填</b>,只拦程序目录和分片暂存目录,
    /// 不拦 <c>\\127.0.0.1\c$</c> 这类绕回本机的共享路径,边界就是能改文件服务器的账号本身。</para>
    /// </summary>
    public string[] AllowedRoots { get; set; } = [];

    /// <summary>分片上传的暂存目录。相对路径<b>按 ContentRoot 解析</b>(与上传根同一基准,不随进程 CWD 漂移)。</summary>
    public string StagingPath { get; set; } = "./data/file-path-staging";

    /// <summary>分片会话的存活期(小时):这么久没有新分片就视为放弃,暂存目录清掉。必须大于 0。</summary>
    public int StagingTtlHours { get; set; } = 24;

    /// <summary>签名直链的默认有效分钟数,签发时可以另传。必须大于 0。</summary>
    public int LinkTtlMinutes { get; set; } = 60;
}
