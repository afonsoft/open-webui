using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using OpenWebUI.Client.Components;
using OpenWebUI.Client.Services;

namespace OpenWebUI.Client.Tests;

/// <summary>
/// SPEC-20261009-web-ide-surface RF-001: render do explorer com tree mockada
/// (dirs primeiro, dir colapsado sem expandir), lazy-load por clique e
/// callback de abertura de arquivo.
/// </summary>
[TestFixture]
public sealed class IdeExplorerTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> route)
        : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request.RequestUri?.PathAndQuery ?? "");
            return Task.FromResult(route(request));
        }
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(body,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            Encoding.UTF8, "application/json"),
    };

    private static (Bunit.BunitContext Ctx, FakeHandler Handler) Setup(
        Func<HttpRequestMessage, HttpResponseMessage> route)
    {
        var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var handler = new FakeHandler(route);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var l10n = new LocalizationService(http, ctx.JSInterop.JSRuntime);
        var auth = new AuthService(http,
            new BrowserStorage(ctx.JSInterop.JSRuntime), l10n);
        ctx.Services.AddSingleton(new ApiService(http, auth));
        ctx.Services.AddSingleton(l10n);
        ctx.Services.AddSingleton(new DialogService());
        return (ctx, handler);
    }

    private static object Tree(params object[] entries) => new
    {
        path = "",
        entries,
        nextCursor = (string?)null,
        truncated = false,
    };

    private static object Entry(string name, string path, string type, bool collapsed = false) =>
        new { name, path, type, size = (long?)null, collapsed };

    [Test]
    public void Explorer_Renderiza_Raiz_DirsPrimeiro()
    {
        var (ctx, _) = Setup(_ => Json(Tree(
            Entry("src", "src", "dir"),
            Entry("README.md", "README.md", "file"))));
        using var cut = ctx.Render<IdeExplorer>();
        var items = cut.FindAll("[role=treeitem]");
        Assert.That(items, Has.Count.EqualTo(2));
        Assert.That(items[0].TextContent, Does.Contain("src"));
        Assert.That(items[1].TextContent, Does.Contain("README.md"));
    }

    [Test]
    public void Explorer_DirColapsado_NaoExpande()
    {
        var (ctx, handler) = Setup(_ => Json(Tree(
            Entry(".git", ".git", "dir", collapsed: true))));
        using var cut = ctx.Render<IdeExplorer>();
        cut.Find("[role=treeitem]").Click();
        // Dir colapsado (.git) não dispara lazy-load nem abre filhos.
        Assert.That(handler.Calls, Has.Count.EqualTo(1));
    }

    [Test]
    public void Explorer_CliqueArquivo_DisparaCallback()
    {
        var (ctx, _) = Setup(_ => Json(Tree(
            Entry("a.cs", "a.cs", "file"))));
        string? opened = null;
        using var cut = ctx.Render<IdeExplorer>(p =>
            p.Add(x => x.OnOpenFile, path => opened = path));
        cut.Find("[role=treeitem]").Click();
        Assert.That(opened, Is.EqualTo("a.cs"));
    }

    [Test]
    public void Explorer_Vazio_MostraEmptyState()
    {
        var (ctx, _) = Setup(_ => Json(Tree()));
        using var cut = ctx.Render<IdeExplorer>();
        Assert.That(cut.Markup, Does.Contain("ide.empty_tree"));
    }
}
