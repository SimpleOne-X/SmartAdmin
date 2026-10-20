using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 文件路径用例的运行环境:两个临时目录(<see cref="Root"/> 充当文件服务器根路径并已放进白名单,
/// <see cref="Staging"/> 是分片暂存目录)、一个已换上 <see cref="FakeFileServerConnector"/> 的测试宿主。
/// 释放时关闭宿主并删掉两个目录。
/// </summary>
public sealed class FilePathTestEnv : IAsyncDisposable
{
    private readonly ConcurrentBag<IServiceScope> _scopes = [];

    private FilePathTestEnv(string root, string staging, AdminAppFactory factory, FakeFileServerConnector connector)
    {
        Root = root;
        Staging = staging;
        Factory = factory;
        Connector = connector;
    }

    public string Root { get; }

    public string Staging { get; }

    public AdminAppFactory Factory { get; }

    public FakeFileServerConnector Connector { get; }

    /// <param name="supportsCredentials">替身连接器是否声明支持账号连接(决定「填了账号必须是共享路径」是否生效)。</param>
    /// <param name="overrides">在换连接器之后追加的服务覆盖。</param>
    /// <param name="disabledModules">
    /// 在测试工厂默认禁用的 <c>Dict</c> 之外再禁用的模块(<c>Api:DisabledModules</c>);Dict 始终保持禁用。
    /// </param>
    /// <param name="extraRoots">
    /// 追加进白名单的根路径。共享路径写成 <c>//nas01/files</c>:Windows 和 Linux 都认它是绝对路径;
    /// 用到它的用例要让替身连接器失败,免得真去访问这台主机。
    /// </param>
    public static FilePathTestEnv Create(
        bool supportsCredentials = false, Action<IServiceCollection>? overrides = null,
        string[]? disabledModules = null, params string[] extraRoots)
    {
        var root = Directory.CreateTempSubdirectory("smart-fp-root-").FullName;
        var staging = Directory.CreateTempSubdirectory("smart-fp-staging-").FullName;
        var connector = new FakeFileServerConnector { SupportsCredentials = supportsCredentials };
        var settings = new Dictionary<string, string?>
        {
            ["SmartAdmin:FilePath:AllowedRoots:0"] = root,
            ["SmartAdmin:FilePath:StagingPath"] = staging,
        };
        for (var i = 0; i < extraRoots.Length; i++) settings[$"SmartAdmin:FilePath:AllowedRoots:{i + 1}"] = extraRoots[i];
        var factory = new AdminAppFactory
        {
            DisabledModules = ["Dict", .. disabledModules ?? []],   // Dict 由测试工厂默认禁用,初始化器会整体覆盖它,这里补回
            Settings = settings,
            Overrides = s =>
            {
                s.Replace(ServiceDescriptor.Singleton<IFileServerConnector>(connector));
                overrides?.Invoke(s);
            },
        };
        return new FilePathTestEnv(root, staging, factory, connector);
    }

    /// <summary>在一个新 scope 里解析服务;scope 随环境一起释放。</summary>
    public T Service<T>() where T : notnull
    {
        var scope = Factory.Services.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    /// <summary>登记一台根路径就是 <see cref="Root"/> 的文件服务器,返回 Id。</summary>
    public Task<long> AddServerAsync(string? name = null) =>
        Service<IFileServerService>().AddAsync(new FileServerSaveInput(
            name ?? $"srv-{Guid.NewGuid():N}", Root, null, null, true, null));

    /// <summary>
    /// 绕过配置服务直接插一行可写的文件路径(文件夹 <c>Docs</c>,后缀与大小上限默认 <c>.pdf</c>、10 MB,可传 null),角色串原样写入——
    /// 用来造「种子数据、手工改库」才会出现的非规范值。不失效缓存,所以要在第一次解析之前插完。
    /// </summary>
    public async Task<(long Id, string Code)> AddRawPathAsync(
        long serverId, string? uploadRoleIds, string? downloadRoleIds, string? extensions = ".pdf", int? maxSizeMb = 10)
    {
        var row = new SysFilePath
        {
            Code = $"raw-{Guid.NewGuid():N}", Name = "手工插入", ServerId = serverId, Folder = "Docs",
            AllowedExtensions = extensions, MaxSizeMb = maxSizeMb, UploadRoleIds = uploadRoleIds, DownloadRoleIds = downloadRoleIds, Enabled = true,
        };
        await Service<IRepository<SysFilePath>>().InsertAsync(row);
        return (row.Id, row.Code);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes) scope.Dispose();
        Factory.Dispose();
        foreach (var dir in new[] { Root, Staging })
            try { Directory.Delete(dir, recursive: true); } catch { /* 尽力而为 */ }
        return ValueTask.CompletedTask;
    }
}
