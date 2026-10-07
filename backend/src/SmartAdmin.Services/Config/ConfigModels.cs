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

    /// <summary>是否显示登录页 Hero 亮点</summary>
    public bool ShowFeatures { get; init; } = true;

    /// <summary>是否启用登录验证码(运行时配置驱动;前端据此决定登录页是否展示验证码)。</summary>
    public bool CaptchaEnabled { get; init; }

    /// <summary>是否启用短信验证码免密登录(运行时配置驱动;前端据此决定登录页是否展示短信登录入口)。</summary>
    public bool SmsLoginEnabled { get; init; }

    /// <summary>登录后页面的水印设置(只有版式参数;每个人印什么由前端按登录用户拼)。</summary>
    public WatermarkOutput Watermark { get; init; } = new();
}

/// <summary>水印设置。取值越界或写错时按默认值或边界收口,前端拿到的永远是合法值。</summary>
public record WatermarkOutput
{
    /// <summary>是否启用</summary>
    public bool Enabled { get; init; }

    /// <summary>内容项,已按固定顺序排好:name / account / org / phone / time / text</summary>
    public IReadOnlyList<string> Fields { get; init; } = ["name", "account", "time"];

    /// <summary>自定义文字(内容项含 text 才印)</summary>
    public string Text { get; init; } = "";

    /// <summary>时间格式:YYYY-MM-DD / YYYY-MM-DD HH:mm / YYYY-MM-DD HH:mm:ss / MM-DD HH:mm</summary>
    public string TimeFormat { get; init; } = "YYYY-MM-DD HH:mm";

    /// <summary>排版:single 单行 / multi 每项一行</summary>
    public string Layout { get; init; } = "single";

    /// <summary>字号 px(12–28)</summary>
    public int FontSize { get; init; } = 14;

    /// <summary>不透明度百分比(2–30)</summary>
    public int Opacity { get; init; } = 8;

    /// <summary>倾斜角度(-45–45)</summary>
    public int Rotate { get; init; } = -20;

    /// <summary>疏密:sparse / normal / dense</summary>
    public string Density { get; init; } = "normal";

    /// <summary>错位排列</summary>
    public bool Cross { get; init; } = true;
}

/// <summary>登录页 Hero 的可选覆盖值;空值代表使用前端内置文案。</summary>
public record LoginHeroOutput
{
    /// <summary>主标题全文</summary>
    public string? Headline { get; init; }

    /// <summary>主标题中需要强调色的原文片段</summary>
    public string? Highlight { get; init; }

    /// <summary>亮点清单</summary>
    public IReadOnlyList<string> Features { get; init; } = [];
}
