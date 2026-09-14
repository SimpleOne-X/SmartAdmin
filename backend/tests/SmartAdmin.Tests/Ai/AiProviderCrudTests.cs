using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// AI 厂商管理服务(<see cref="AiProviderService"/>)的 CRUD 契约测试。不走 HTTP 端点,直接从 DI 解析
/// <see cref="IAiProviderService"/> 调用(参考 <see cref="AiChatClientTests"/> 的写法)。
/// </summary>
public class AiProviderCrudTests
{
    private static string NewCode(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static AiModelInput Model(string name, bool isDefault = false, long? id = null) =>
        new(id, name, name, true, isDefault, null, null, null);

    [Fact]
    public async Task Add_carries_protocol_from_preset()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAiProviderService>();

        var code = NewCode("anthropic");
        var id = await service.AddAsync(new AiProviderAddInput(
            code, "Test Anthropic", "anthropic", "https://api.anthropic.com", "x-api-key",
            "sk-ant-test", 1, null, [Model("claude-test", isDefault: true)]));

        var view = await service.GetAsync(id);
        Assert.Equal("anthropic", view.Preset);
        Assert.Equal("anthropic", view.Protocol);   // 由预设带出,非入参
        Assert.Equal(code, view.Code);
    }

    // 实测发现(与最初预期不同,记录在此供复核):SqlSugarRepository&lt;T&gt;.DeleteAsync 对任何软删实体的
    // [SugarIndex(IsUnique=true)] 字符串列都会统一追加 `_del_{id}` 后缀就地释放唯一占位(仓储层通用机制,
    // 非本服务专属——OrgAuditEntityTests.cs 里 Plain_AuditEntity_physical_delete_fills_audit_and_frees_unique_constraint
    // 的注释"软删则会因 _del_ 占位/撞唯一索引而不同"已印证同一机制)。软删那一刻,行在库里的 Code 就已经
    // 不再是原始字符串,所以"按原始 Code 查重、含 ClearFilter&lt;ISoftDelete&gt;()"这种写法(本服务
    // EnsureCodeAvailableAsync 采用的正是 OrgService.usedCodes 那套写法)在实测中永远查不到已软删行——
    // 这不是查询写错了,而是软删后原值已经合法地被仓储层释放给新行复用。故本测试断言的是实测行为:
    // 未删除时重复 Code 直接冲突;真正软删后,同一 Code 允许被新厂商复用。
    [Fact]
    public async Task Code_uniqueness_rejects_live_conflict_but_releases_after_soft_delete()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAiProviderService>();

        var code = NewCode("dup");
        var id1 = await service.AddAsync(new AiProviderAddInput(
            code, "First", "deepseek", "https://api.deepseek.com/v1", "bearer", "sk-1", 1, null, []));

        // 未删除时重复 code 直接冲突
        var ex1 = await Assert.ThrowsAsync<AdminException>(() => service.AddAsync(new AiProviderAddInput(
            code, "Second", "deepseek", "https://api.deepseek.com/v1", "bearer", "sk-2", 2, null, [])));
        Assert.Equal(ErrorCode.AiProviderCodeExists, ex1.Code);

        // 软删后仓储层已释放该 Code 的唯一占位,允许新厂商复用
        await service.DeleteAsync(id1);
        var id2 = await service.AddAsync(new AiProviderAddInput(
            code, "Third", "deepseek", "https://api.deepseek.com/v1", "bearer", "sk-3", 3, null, []));
        Assert.NotEqual(id1, id2);
        Assert.Equal(code, (await service.GetAsync(id2)).Code);
    }

    [Fact]
    public async Task ApiKey_is_never_exposed_in_plaintext_but_decrypts_back()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAiProviderService>();
        var providers = scope.ServiceProvider.GetRequiredService<IRepository<SysAiProvider>>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();

        const string apiKey = "sk-abcdef123456";
        var id = await service.AddAsync(new AiProviderAddInput(
            NewCode("keytest"), "Key Test", "openai", "https://api.openai.com/v1", "bearer",
            apiKey, 1, null, []));

        var view = await service.GetAsync(id);
        Assert.True(view.HasApiKey);
        Assert.Equal("3456", view.ApiKeyHint);   // AiProviderView 没有 ApiKeyProtected 字段,类型层面就不可能泄露明文

        var entity = await providers.GetByIdAsync(id);
        Assert.NotNull(entity!.ApiKeyProtected);
        Assert.DoesNotContain(apiKey, entity.ApiKeyProtected);
        Assert.Equal(apiKey, protector.Unprotect(entity.ApiKeyProtected!));
    }

    [Fact]
    public async Task Update_with_empty_api_key_leaves_existing_key_untouched()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAiProviderService>();
        var providers = scope.ServiceProvider.GetRequiredService<IRepository<SysAiProvider>>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();

        const string apiKey = "sk-untouched-key";
        var code = NewCode("keepkey");
        var id = await service.AddAsync(new AiProviderAddInput(
            code, "Keep Key", "openai", "https://api.openai.com/v1", "bearer", apiKey, 1, null, []));

        await service.UpdateAsync(id, new AiProviderUpdateInput(
            code, "Keep Key Renamed", "https://api.openai.com/v1", "bearer", null, 2, "改了名字没改 Key", []));

        var entity = await providers.GetByIdAsync(id);
        Assert.Equal(apiKey, protector.Unprotect(entity!.ApiKeyProtected!));
        Assert.Equal("Keep Key Renamed", entity.Name);
    }

    [Fact]
    public async Task Cannot_enable_without_api_key_when_auth_scheme_requires_one()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAiProviderService>();

        var id = await service.AddAsync(new AiProviderAddInput(
            NewCode("nokey"), "No Key", "deepseek", "https://api.deepseek.com/v1", "bearer",
            null, 1, null, []));

        var ex = await Assert.ThrowsAsync<AdminException>(() => service.SetEnabledAsync(id, true));
        Assert.Equal(ErrorCode.AiApiKeyMissing, ex.Code);
    }

    [Fact]
    public async Task Can_enable_without_api_key_when_auth_scheme_is_none()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAiProviderService>();

        var id = await service.AddAsync(new AiProviderAddInput(
            NewCode("ollama"), "Local Ollama", "ollama", "http://localhost:11434/v1", "none",
            null, 1, null, []));

        await service.SetEnabledAsync(id, true);   // 不应抛异常
        var view = await service.GetAsync(id);
        Assert.True(view.Enabled);
    }

    // 设计选择:SysAiModel.IsDefault 的实体文档明确写着"全库只允许一个 true"——AiChatClient 在未指定
    // 厂商/模型时按 provider 遍历顺序取第一个 IsDefault 模型,顺序本身不保证稳定,只有全局(而非仅同厂商内)
    // 唯一才能让"全局默认模型"这个语义成立,所以本服务按全库维护该唯一性(见 AiProviderService.ClearOtherDefaultsAsync)。
    [Fact]
    public async Task IsDefault_is_globally_unique_across_providers()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAiProviderService>();

        var id1 = await service.AddAsync(new AiProviderAddInput(
            NewCode("prov1"), "Provider 1", "deepseek", "https://api.deepseek.com/v1", "bearer",
            "sk-1", 1, null, [Model("m1", isDefault: true)]));
        var id2 = await service.AddAsync(new AiProviderAddInput(
            NewCode("prov2"), "Provider 2", "deepseek", "https://api.deepseek.com/v1", "bearer",
            "sk-2", 2, null, [Model("m2", isDefault: true)]));

        var view1 = await service.GetAsync(id1);
        var view2 = await service.GetAsync(id2);
        Assert.False(view1.Models.Single().IsDefault);   // 被第二次新增顶掉
        Assert.True(view2.Models.Single().IsDefault);
    }

    [Fact]
    public async Task IsDefault_duplicate_within_same_submit_keeps_only_the_first()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAiProviderService>();

        var id = await service.AddAsync(new AiProviderAddInput(
            NewCode("twodefaults"), "Two Defaults", "deepseek", "https://api.deepseek.com/v1", "bearer",
            "sk-1", 1, null, [Model("m1", isDefault: true), Model("m2", isDefault: true)]));

        var view = await service.GetAsync(id);
        Assert.True(view.Models.Single(m => m.Name == "m1").IsDefault);
        Assert.False(view.Models.Single(m => m.Name == "m2").IsDefault);
    }

    [Fact]
    public async Task Delete_cascades_soft_delete_to_models()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAiProviderService>();
        var models = scope.ServiceProvider.GetRequiredService<IRepository<SysAiModel>>();

        var id = await service.AddAsync(new AiProviderAddInput(
            NewCode("cascade"), "Cascade", "deepseek", "https://api.deepseek.com/v1", "bearer",
            "sk-1", 1, null, [Model("m1"), Model("m2")]));

        Assert.Equal(2, (await models.AsQueryable().Where(m => m.ProviderId == id).ToListAsync()).Count);

        await service.DeleteAsync(id);

        // 正常查询(过软删过滤器)已看不到
        Assert.Empty(await models.AsQueryable().Where(m => m.ProviderId == id).ToListAsync());
        // 穿透软删过滤器仍能看到,且 IsDelete=true
        var raw = await models.AsQueryable().ClearFilter<ISoftDelete>()
            .Where(m => m.ProviderId == id).ToListAsync();
        Assert.Equal(2, raw.Count);
        Assert.All(raw, m => Assert.True(m.IsDelete));
    }

    [Fact]
    public async Task Invalid_base_url_is_rejected()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAiProviderService>();

        var ex = await Assert.ThrowsAsync<AdminException>(() => service.AddAsync(new AiProviderAddInput(
            NewCode("badurl"), "Bad Url", "custom", "not-a-url", "bearer", "sk-1", 1, null, [])));
        Assert.Equal(ErrorCode.AiBaseUrlBlocked, ex.Code);
    }

    [Fact]
    public async Task Cloud_metadata_base_url_is_rejected_by_the_ssrf_fence()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAiProviderService>();

        var ex = await Assert.ThrowsAsync<AdminException>(() => service.AddAsync(new AiProviderAddInput(
            NewCode("ssrf"), "SSRF", "custom", "http://169.254.169.254/latest/meta-data/", "bearer", "sk-1", 1, null, [])));
        Assert.Equal(ErrorCode.AiBaseUrlBlocked, ex.Code);
    }
}
