using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>系统配置新增/编辑入参(增改共用同一份字段;<c>ConfigKey</c> 编辑时不生效,见 <see cref="ConfigService.UpdateAsync"/>)。</summary>
public record ConfigInput
{
    /// <summary>配置键(唯一,创建后不可修改)</summary>
    public string ConfigKey { get; init; } = "";

    /// <summary>配置值</summary>
    public string? ConfigValue { get; init; }

    /// <summary>配置名称</summary>
    public string Name { get; init; } = "";

    /// <summary>分组编码(可选)</summary>
    public string? GroupCode { get; init; }

    /// <summary>排序(小在前)</summary>
    public int Sort { get; init; }

    /// <summary>备注</summary>
    public string? Remark { get; init; }
}

/// <summary>系统配置分页查询入参:在通用分页基础上加名称/键模糊过滤 + 分组精确/排除过滤。</summary>
public record ConfigPageInput : PageInputBase
{
    /// <summary>配置键(模糊匹配,可选)</summary>
    public string? ConfigKey { get; init; }

    /// <summary>配置名称(模糊匹配,可选)</summary>
    public string? Name { get; init; }

    /// <summary>分组编码(精确匹配,可选)</summary>
    public string? GroupCode { get; init; }

    /// <summary>排除的分组编码(可选;用于只列自定义配置等视图)。</summary>
    public string[]? ExcludedGroupCodes { get; init; }

    /// <summary>排除的配置键(可选;配置中心「高级」页用它去掉结构化表单已认领的键)。</summary>
    public string[]? ExcludedKeys { get; init; }
}

/// <summary>批量存值入参项:分类配置中心的结构化表单按键回写<b>值</b>(不碰 Name/GroupCode/Sort)。</summary>
public record ConfigBatchItem
{
    /// <summary>配置键</summary>
    public string ConfigKey { get; init; } = "";

    /// <summary>配置值</summary>
    public string? ConfigValue { get; init; }
}

/// <summary>站点信息(匿名可读的展示类配置白名单;登录前/无配置读权限的用户也能取)。</summary>
public record SiteInfoOutput
{
    /// <summary>站点标题(浏览器标题/登录页展示名)</summary>
    public string? Title { get; init; }

    /// <summary>登录页副标题(留空则前端回退内置文案)</summary>
    public string? Subtitle { get; init; }

    /// <summary>版权信息(登录页页脚版权名;留空则前端回退站点标题)</summary>
    public string? Copyright { get; init; }

    /// <summary>版权链接(版权名的超链接;留空则纯文本)</summary>
    public string? CopyrightUrl { get; init; }

    /// <summary>站点 Logo 图片地址(登录页、侧栏、顶栏、应用选择页统一显示;留空则前端回退内置矢量 logo)</summary>
    public string? Logo { get; init; }

    /// <summary>登录页 Hero 文案(按 locale 分组;缺失字段由前端回退内置 i18n)</summary>
    public Dictionary<string, LoginHeroOutput> LoginHero { get; init; } = [];

    /// <summary>是否显示登录页 Hero 卖点</summary>
    public bool ShowFeatures { get; init; } = true;

    /// <summary>是否启用登录验证码(运行时配置驱动;前端据此决定登录页是否展示验证码)。</summary>
    public bool CaptchaEnabled { get; init; }

    /// <summary>是否启用短信验证码免密登录(运行时配置驱动;前端据此决定登录页是否展示短信登录入口)。</summary>
    public bool SmsLoginEnabled { get; init; }
}

/// <summary>登录页 Hero 的可选覆盖值;空值代表使用前端内置文案。</summary>
public record LoginHeroOutput
{
    /// <summary>主标题全文</summary>
    public string? Headline { get; init; }

    /// <summary>主标题中需要强调色的原文片段</summary>
    public string? Highlight { get; init; }

    /// <summary>卖点清单</summary>
    public IReadOnlyList<string> Features { get; init; } = [];
}
