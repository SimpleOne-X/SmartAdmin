using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// AI 厂商管理服务——运维在「AI 模型」页维护厂商 + 模型清单的标准 CRUD,外加启停与测试连接。
/// <see cref="Core.IAiChatClient"/>(批次 2)只读本服务写入的数据(经 <see cref="CacheKeys.AiProviders"/> 缓存),
/// 本服务的增删改因此必须失效该缓存并广播 <see cref="AiProviderChangedEvent"/>(见各方法说明)。
/// </summary>
public interface IAiProviderService
{
    /// <summary>分页查询厂商(含各自模型清单),按 Keyword 模糊过滤 Code/Name + Enabled 精确过滤,按 Sort 升序返回。</summary>
    Task<PagedList<AiProviderView>> PageAsync(AiProviderPageInput input);

    /// <summary>按 Id 取单条(含模型清单),不存在抛 <see cref="ErrorCode.AiProviderNotFound"/>。</summary>
    Task<AiProviderView> GetAsync(long id);

    /// <summary>
    /// 新增厂商。<paramref name="input"/>.Preset 必须命中 <see cref="AiProviderPresets.Find"/>(否则抛
    /// <see cref="ErrorCode.AiPresetNotFound"/>);<c>Protocol</c> 取自预设,不接受入参覆盖,<c>BaseUrl</c>
    /// 取自入参(允许覆盖预设默认值,如 azure-openai 必须改)。Code 唯一性校验含软删行,冲突抛
    /// <see cref="ErrorCode.AiProviderCodeExists"/>。随带的模型清单按 <see cref="AiModelInput"/> 规则整体写入。
    /// 返回新 Id。
    /// </summary>
    Task<long> AddAsync(AiProviderAddInput input);

    /// <summary>
    /// 更新厂商(不含 Preset/Protocol,创建后不可改)。Code 若变更需重新做唯一性校验(同 <see cref="AddAsync"/>);
    /// 模型清单按入参整体同步——Id 为空新增,Id 存在则更新,库中存在但入参未提及的行软删除。不存在抛
    /// <see cref="ErrorCode.AiProviderNotFound"/>。
    /// </summary>
    Task UpdateAsync(long id, AiProviderUpdateInput input);

    /// <summary>软删除厂商,并级联软删其下所有模型行;用量日志不受影响(冗余 ProviderCode 字段)。</summary>
    Task DeleteAsync(long id);

    /// <summary>
    /// 启停厂商。启用时若 <c>AuthScheme != "none"</c> 且未配置 Key,抛 <see cref="ErrorCode.AiApiKeyMissing"/>。
    /// </summary>
    Task SetEnabledAsync(long id, bool enabled);

    /// <summary>
    /// 测试连接:取该厂商的默认可用模型(IsDefault&amp;&amp;Enabled,没有则取第一个 Enabled 的模型,都没有则直接
    /// 返回失败结果,不抛异常),发一条极短对话("ping",MaxTokens=8,Scene="system.test")。调用失败(上游错误
    /// 映射后的 <see cref="AdminException"/>)同样包成失败结果返回,不让异常冒泡——前端要能看到失败原因。
    /// </summary>
    Task<AiTestResult> TestAsync(long id);

    /// <summary>厂商预置清单(协议/BaseUrl/鉴权方式/起步模型),供新增页选择预设时展示。</summary>
    Task<IReadOnlyList<AiProviderPreset>> PresetsAsync();
}
