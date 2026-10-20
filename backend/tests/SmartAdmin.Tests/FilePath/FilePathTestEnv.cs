using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartAdmin.Services;

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
    public static FilePathTestEnv Create(bool supportsCredentials = false, Action<IServiceCollection>? overrides = null)
    {
        var root = Directory.CreateTempSubdirectory("smart-fp-root-").FullName;
        var staging = Directory.CreateTempSubdirectory("smart-fp-staging-").FullName;
        var connector = new FakeFileServerConnector { SupportsCredentials = supportsCredentials };
        var factory = new AdminAppFactory
        {
            Settings = new Dictionary<string, string?>
            {
                ["SmartAdmin:FilePath:AllowedRoots:0"] = root,
                ["SmartAdmin:FilePath:StagingPath"] = staging,
            },
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

    public ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes) scope.Dispose();
        Factory.Dispose();
        foreach (var dir in new[] { Root, Staging })
            try { Directory.Delete(dir, recursive: true); } catch { /* 尽力而为 */ }
        return ValueTask.CompletedTask;
    }
}
