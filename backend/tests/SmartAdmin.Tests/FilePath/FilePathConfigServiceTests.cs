using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>文件路径配置:编码格式与唯一、可写/只读行的必填与丢弃规则、后缀与文件夹规范化、角色存储、缓存失效。</summary>
public class FilePathConfigServiceTests
{
    private static string NewCode() => $"c-{Guid.NewGuid():N}";

    private static FilePathAddInput Input(
        long serverId, string? code = null, string? folder = "Docs",
        FilePathNamingRule naming = FilePathNamingRule.YearMonthOriginal,
        FilePathConflictMode onConflict = FilePathConflictMode.Reject, bool readOnly = false,
        string? extensions = ".pdf", int? maxSizeMb = 10,
        IReadOnlyList<long>? upload = null, IReadOnlyList<long>? download = null, bool enabled = true) =>
        new(code ?? NewCode(), "订单附件", serverId, folder, naming, onConflict, readOnly, extensions, maxSizeMb,
            upload, download, enabled, null);

    private static FilePathUpdateInput ToUpdate(FilePathAddInput i) =>
        new(i.Name, i.ServerId, i.Folder, i.NamingRule, i.OnConflict, i.ReadOnly, i.AllowedExtensions, i.MaxSizeMb,
            i.UploadRoleIds, i.DownloadRoleIds, i.Enabled, i.Remark);

    private static async Task<AdminException> Rejected(Task task) => await Assert.ThrowsAsync<AdminException>(() => task);

    [Fact]
    public async Task Code_format_is_enforced()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFilePathConfigService>();
        var serverId = await env.AddServerAsync();

        foreach (var bad in new[] { "a b", "中文", new string('a', 65), "", "ok\n" })
        {
            var ex = await Rejected(service.AddAsync(Input(serverId, code: bad)));
            Assert.Equal(ErrorCode.FilePathCodeInvalid, ex.Code);
        }

        var id = await service.AddAsync(Input(serverId, code: "Order.Attachment_1-x"));
        Assert.Equal("Order.Attachment_1-x", (await service.GetAsync(id)).Code);
        await service.AddAsync(Input(serverId, code: new string('a', 64)));
    }

    [Fact]
    public async Task Code_unique_and_released_after_delete()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFilePathConfigService>();
        var serverId = await env.AddServerAsync();
        var code = NewCode();
        var id = await service.AddAsync(Input(serverId, code));

        var ex = await Rejected(service.AddAsync(Input(serverId, code)));
        Assert.Equal(ErrorCode.FilePathCodeExists, ex.Code);

        // 软删后唯一列被追加后缀,编码即可再用
        await service.DeleteAsync(id);
        Assert.NotEqual(id, await service.AddAsync(Input(serverId, code)));
    }

    [Fact]
    public async Task Writable_row_requires_extensions_and_size()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFilePathConfigService>();
        var serverId = await env.AddServerAsync();

        var incomplete = new[]
        {
            Input(serverId, extensions: null),
            Input(serverId, extensions: ""),
            Input(serverId, extensions: " , "),
            Input(serverId, maxSizeMb: null),
            Input(serverId, maxSizeMb: 0),
            Input(serverId, maxSizeMb: -1),
        };
        foreach (var input in incomplete)
        {
            var ex = await Rejected(service.AddAsync(input));
            Assert.Equal(ErrorCode.FilePathRuleIncomplete, ex.Code);
        }

        // 更新同样要求必填
        var id = await service.AddAsync(Input(serverId));
        var updateEx = await Rejected(service.UpdateAsync(id, ToUpdate(Input(serverId, maxSizeMb: 0))));
        Assert.Equal(ErrorCode.FilePathRuleIncomplete, updateEx.Code);
    }

    [Fact]
    public async Task Read_only_row_drops_extensions_and_size()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFilePathConfigService>();
        var serverId = await env.AddServerAsync();

        var id = await service.AddAsync(Input(serverId, readOnly: true, extensions: ".pdf", maxSizeMb: 10));

        var view = await service.GetAsync(id);
        Assert.True(view.ReadOnly);
        Assert.Null(view.AllowedExtensions);
        Assert.Null(view.MaxSizeMb);

        // 只读行即使没填也能保存
        await service.AddAsync(Input(serverId, readOnly: true, extensions: null, maxSizeMb: null));
    }

    [Fact]
    public async Task Extensions_are_normalized()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFilePathConfigService>();
        var serverId = await env.AddServerAsync();

        var id = await service.AddAsync(Input(serverId, extensions: " .PDF,.png"));
        Assert.Equal(".pdf,.png", (await service.GetAsync(id)).AllowedExtensions);

        var ex = await Rejected(service.AddAsync(Input(serverId, extensions: "pdf")));
        Assert.Equal(ErrorCode.FilePathExtensionsInvalid, ex.Code);
    }

    [Fact]
    public async Task Folder_is_validated()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFilePathConfigService>();
        var serverId = await env.AddServerAsync();

        foreach (var bad in new[] { "../x", ".hidden/x" })
        {
            var ex = await Rejected(service.AddAsync(Input(serverId, folder: bad)));
            Assert.Equal(ErrorCode.FilePathInvalidPath, ex.Code);
        }

        var id = await service.AddAsync(Input(serverId, folder: @"Order\Attachment"));
        Assert.Equal("Order/Attachment", (await service.GetAsync(id)).Folder);

        // 空 = 根路径本身,统一存成 null
        foreach (var empty in new[] { null, "" })
        {
            var rootId = await service.AddAsync(Input(serverId, folder: empty));
            Assert.Null((await service.GetAsync(rootId)).Folder);
        }
    }

    [Fact]
    public async Task Conflict_mode_only_applies_to_business_path()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFilePathConfigService>();
        var serverId = await env.AddServerAsync();

        var ignored = await service.AddAsync(Input(serverId,
            naming: FilePathNamingRule.YearMonthOriginal, onConflict: FilePathConflictMode.Overwrite));
        Assert.Equal(FilePathConflictMode.Reject, (await service.GetAsync(ignored)).OnConflict);

        var kept = await service.AddAsync(Input(serverId,
            naming: FilePathNamingRule.BusinessPath, onConflict: FilePathConflictMode.Overwrite));
        Assert.Equal(FilePathConflictMode.Overwrite, (await service.GetAsync(kept)).OnConflict);
    }

    [Fact]
    public async Task Role_ids_round_trip()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFilePathConfigService>();
        var serverId = await env.AddServerAsync();

        var id = await service.AddAsync(Input(serverId, upload: [5, 3, 5], download: []));

        var view = await service.GetAsync(id);
        Assert.Equal([3L, 5L], view.UploadRoleIds);
        Assert.Empty(view.DownloadRoleIds);

        // 库里是升序去重的逗号串,空集合存 null
        var row = (await env.Service<IRepository<SysFilePath>>().GetByIdAsync(id))!;
        Assert.Equal("3,5", row.UploadRoleIds);
        Assert.Null(row.DownloadRoleIds);
    }

    [Fact]
    public async Task Unknown_server_is_rejected()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFilePathConfigService>();

        var ex = await Rejected(service.AddAsync(Input(999)));
        Assert.Equal(ErrorCode.FileServerNotFound, ex.Code);

        var serverId = await env.AddServerAsync();
        var id = await service.AddAsync(Input(serverId));
        var updateEx = await Rejected(service.UpdateAsync(id, ToUpdate(Input(999))));
        Assert.Equal(ErrorCode.FileServerNotFound, updateEx.Code);
    }

    [Fact]
    public async Task Update_changes_fields_but_keeps_code()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFilePathConfigService>();
        var serverId = await env.AddServerAsync();
        var code = NewCode();
        var id = await service.AddAsync(Input(serverId, code));

        await service.UpdateAsync(id, ToUpdate(Input(serverId, folder: "Other", extensions: ".docx", maxSizeMb: 5, enabled: false)));

        var view = await service.GetAsync(id);
        Assert.Equal(code, view.Code);
        Assert.Equal("Other", view.Folder);
        Assert.Equal(".docx", view.AllowedExtensions);
        Assert.Equal(5, view.MaxSizeMb);
        Assert.False(view.Enabled);
    }

    [Fact]
    public async Task Unknown_id_is_not_found()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFilePathConfigService>();
        var serverId = await env.AddServerAsync();

        Assert.Equal(ErrorCode.FilePathNotFound, (await Rejected(service.GetAsync(1))).Code);
        Assert.Equal(ErrorCode.FilePathNotFound, (await Rejected(service.UpdateAsync(1, ToUpdate(Input(serverId))))).Code);
        Assert.Equal(ErrorCode.FilePathNotFound, (await Rejected(service.DeleteAsync(1))).Code);
    }

    [Fact]
    public async Task Page_filters_and_carries_server_name()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFilePathConfigService>();
        var serverA = await env.AddServerAsync("server-a-" + Guid.NewGuid().ToString("N")[..8]);
        var serverB = await env.AddServerAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var a = await service.AddAsync(Input(serverA, $"a-{tag}"));
        var b = await service.AddAsync(Input(serverB, $"b-{tag}", enabled: false));

        var byKeyword = await service.PageAsync(new FilePathPageInput { Keyword = tag });
        Assert.Equal(new[] { a, b }.Order(), byKeyword.Items.Select(v => v.Id).Order());
        var serverAName = (await env.Service<IFileServerService>().GetAsync(serverA)).Name;
        Assert.Equal(serverAName, byKeyword.Items.Single(v => v.Id == a).ServerName);

        var byServer = await service.PageAsync(new FilePathPageInput { Keyword = tag, ServerId = serverB });
        Assert.Equal([b], byServer.Items.Select(v => v.Id));

        var enabledOnly = await service.PageAsync(new FilePathPageInput { Keyword = tag, Enabled = true });
        Assert.Equal([a], enabledOnly.Items.Select(v => v.Id));
    }

    [Fact]
    public async Task Environment_reports_roots_and_credential_support()
    {
        await using var env = FilePathTestEnv.Create(supportsCredentials: true);
        var service = env.Service<IFilePathConfigService>();

        var info = service.GetEnvironment();
        Assert.Contains(env.Root, info.AllowedRoots);
        Assert.True(info.SupportsCredentials);

        env.Connector.SupportsCredentials = false;
        Assert.False(service.GetEnvironment().SupportsCredentials);
    }

    [Fact]
    public async Task Mutations_invalidate_path_cache()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFilePathConfigService>();
        var cache = env.Service<ICacheProvider>();
        var serverId = await env.AddServerAsync();

        async Task Prime() => await cache.SetAsync(CacheKeys.FilePaths(), "stale");
        async Task AssertEvicted(string step) =>
            Assert.True(await cache.GetAsync<string>(CacheKeys.FilePaths()) is null, $"{step}之后缓存应已失效");

        await Prime();
        var input = Input(serverId);
        var id = await service.AddAsync(input);
        await AssertEvicted("新增");

        await Prime();
        await service.UpdateAsync(id, ToUpdate(input));
        await AssertEvicted("修改");

        await Prime();
        await service.DeleteAsync(id);
        await AssertEvicted("删除");
    }

    [Fact]
    public async Task Corrupt_role_string_row_still_opens_and_can_be_resaved()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFilePathConfigService>();
        var serverId = await env.AddServerAsync();
        var (id, _) = await env.AddRawPathAsync(serverId, "admin", "3;5");

        // 配置页宽松展示:坏项丢掉,行本身仍能打开、列出
        var view = await service.GetAsync(id);
        Assert.Empty(view.UploadRoleIds);
        Assert.Empty(view.DownloadRoleIds);
        var page = await service.PageAsync(new FilePathPageInput { ServerId = serverId });
        Assert.Contains(page.Items, v => v.Id == id);

        // 重存即改写成规范形式
        await service.UpdateAsync(id, ToUpdate(Input(serverId, upload: [8, 2], download: [])));
        var row = (await env.Service<IRepository<SysFilePath>>().GetByIdAsync(id))!;
        Assert.Equal("2,8", row.UploadRoleIds);
        Assert.Null(row.DownloadRoleIds);
    }
}
