using System.Collections.Frozen;

namespace SmartAdmin.Services;

/// <summary>
/// 高敏感权限码内核默认集合。
/// 集合不可变、不可经管理页删除;消费者只能追加自定义码。
/// </summary>
public static class HighSensitivityPermissions
{
    /// <summary>管理员清除用户 MFA。</summary>
    public const string MfaClear = "POST:/api/v1/sys/mfa/clear";

    /// <summary>追加自定义高敏权限码。</summary>
    public const string HighSensAdd = "POST:/api/v1/sys/mfa/high-sensitivity";

    /// <summary>删除自定义高敏权限码。</summary>
    public const string HighSensDelete = "DELETE:/api/v1/sys/mfa/high-sensitivity/{id:long}";

    /// <summary>内核默认高敏权限码(冻结集)。</summary>
    public static FrozenSet<string> Default { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "POST:/api/v1/sys/user",
        "PUT:/api/v1/sys/user/{id}",
        "DELETE:/api/v1/sys/user/{id}",
        "POST:/api/v1/sys/user/batch-delete",
        "PUT:/api/v1/sys/user/{id}/password",
        "PUT:/api/v1/sys/user/{id}/enabled",

        "POST:/api/v1/sys/role/add",
        "PUT:/api/v1/sys/role/{id}",
        "DELETE:/api/v1/sys/role/{id}",
        "POST:/api/v1/sys/role/batch-delete",
        "PUT:/api/v1/sys/role/menu",
        "PUT:/api/v1/sys/role/datascope",
        "PUT:/api/v1/sys/role/users",

        "POST:/api/v1/sys/config",
        "PUT:/api/v1/sys/config/{id}",
        "DELETE:/api/v1/sys/config/{id}",
        "PUT:/api/v1/sys/config/batch",

        "DELETE:/api/v1/sys/session/{sessionid}",

        // 定时任务写操作:SQL 载荷一旦开启,任务就是用应用的完整库权限跑任意语句
        "POST:/api/v1/sys/job",
        "PUT:/api/v1/sys/job/{id}",
        "DELETE:/api/v1/sys/job/{id}",
        "POST:/api/v1/sys/job/batch-delete",
        "POST:/api/v1/sys/job/{id}/run",

        // 菜单写操作:改 Permission 就是在改授权面本身
        "POST:/api/v1/sys/menu/add",
        "PUT:/api/v1/sys/menu/{id}",
        "DELETE:/api/v1/sys/menu/{id}",

        // 回收站彻底删除:不可逆
        "DELETE:/api/v1/sys/recycle/{type}/{id}",

        // AI 厂商写操作:API Key 经手,新增/更新都要求再认证
        "POST:/api/v1/sys/ai/provider/add",
        "PUT:/api/v1/sys/ai/provider/{id}",

        // 第三方登录方式的连接配置:能改这里就能决定谁能登录系统,保存与清除都要求再认证
        "PUT:/api/v1/sys/external-auth/providers/{code}",
        "DELETE:/api/v1/sys/external-auth/providers/{code}",

        MfaClear,
        HighSensAdd,
        HighSensDelete,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否属于内核默认高敏集合(不可移除)。</summary>
    public static bool IsDefault(string permissionCode) =>
        !string.IsNullOrWhiteSpace(permissionCode) && Default.Contains(permissionCode.Trim());
}
