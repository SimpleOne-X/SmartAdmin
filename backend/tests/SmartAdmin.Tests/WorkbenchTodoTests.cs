using System.Net.Http.Headers;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.Testing;

namespace SmartAdmin.Tests;

public class WorkbenchTodoTests
{
    [Fact]
    public async Task NullProvider_returns_empty_summary()
    {
        IWorkbenchTodoProvider provider = new NullWorkbenchTodoProvider();
        var summary = await provider.GetMineAsync(1);
        Assert.Equal(0, summary.TotalCount);
        Assert.Empty(summary.Items);
    }

    [Fact]
    public async Task GetWorkbenchTodo_default_is_empty()
    {
        using var f = new AdminAppFactory();
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await c.LoginToken("superAdmin", "Test@123456"));

        var body = await (await c.GetAsync("/api/v1/personal/workbench/todo")).ReadEnvelope();
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        var data = body.GetProperty("data");
        Assert.Equal(0, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, data.GetProperty("items").GetArrayLength());
    }
}
