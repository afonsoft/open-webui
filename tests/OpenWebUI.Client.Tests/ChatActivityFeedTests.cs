using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Client.Components;
using OpenWebUI.Client.Services;

namespace OpenWebUI.Client.Tests;

/// <summary>
/// SPEC-20261010-chat-activity-feed: ToolActivityMap (nome → linha amigável)
/// e RunActivityFeed (linhas live, collapse "N passos", detalhes por linha).
/// </summary>
[TestFixture]
public sealed class ChatActivityFeedTests
{
    private static BunitContext Setup()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(new LocalizationService(http, ctx.JSInterop.JSRuntime));
        return ctx;
    }

    private static ToolCallView Live(string id, string name, string? args = null,
        bool? ok = null, bool denied = false) => new()
        {
            Call = new RunToolCallEvent(id, name, args),
            Result = ok is null ? null
                : new RunToolResultEvent(id, name, ok.Value, "preview", Denied: denied),
        };

    [Test]
    public void Map_Categorias_EAlvo()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ToolActivityMap.Map("builtin:web_search", "{\"query\":\"release notes dotnet\"}"),
                Is.EqualTo(("chat.activity.web_search", "release notes dotnet")).AsCollection);
            Assert.That(ToolActivityMap.Map("builtin:file_edit", "{\"path\":\"src/x.cs\"}"),
                Is.EqualTo(("chat.activity.edit", "src/x.cs")).AsCollection);
            Assert.That(ToolActivityMap.Map("builtin:shell_exec", "{\"command\":\"ls -la\"}"),
                Is.EqualTo(("chat.activity.exec", "ls -la")).AsCollection);
            Assert.That(ToolActivityMap.Map("builtin:fetch_url", "{\"url\":\"https://ex.com/a\"}"),
                Is.EqualTo(("chat.activity.fetch", "https://ex.com/a")).AsCollection);
            Assert.That(ToolActivityMap.Map("builtin:delegate_task", null).Item1,
                Is.EqualTo("chat.activity.delegate"));
            Assert.That(ToolActivityMap.Map("builtin:file_grep", null).Item1,
                Is.EqualTo("chat.activity.search_code"));
            Assert.That(ToolActivityMap.Map("builtin:generate_image", null).Item1,
                Is.EqualTo("chat.activity.image"));
            Assert.That(ToolActivityMap.Map("builtin:memory_search", null).Item1,
                Is.EqualTo("chat.activity.memory"));
            Assert.That(ToolActivityMap.Map("builtin:ask_user", null).Item1,
                Is.EqualTo("chat.activity.tool"));
            Assert.That(ToolActivityMap.Map("mcp:github/create_issue", null).Item1,
                Is.EqualTo("chat.activity.tool"));
        });
    }

    [Test]
    public void Map_AlvoTruncado_Em40Chars()
    {
        var longQuery = new string('q', 60);
        var (_, target) = ToolActivityMap.Map(
            "builtin:web_search", $"{{\"query\":\"{longQuery}\"}}");
        Assert.That(target, Has.Length.EqualTo(41)); // 40 + reticência
    }

    [Test]
    public void Feed_Live_LinhaAmigavel_ESpinner()
    {
        using var ctx = Setup();
        var calls = new List<ToolCallView>
        {
            Live("1", "builtin:web_search", "{\"query\":\"dotnet 10\"}"),
            Live("2", "builtin:file_edit", "{\"path\":\"a.cs\"}", ok: true),
        };
        var cut = ctx.Render<RunActivityFeed>(p => p
            .Add(c => c.Calls, calls)
            .Add(c => c.Streaming, true)
            .Add(c => c.Collapsed, false));

        Assert.Multiple(() =>
        {
            // Linhas amigáveis, sem nome de tool nem JSON cru.
            Assert.That(cut.Markup, Does.Contain("chat.activity.web_search"));
            Assert.That(cut.Markup, Does.Contain("dotnet 10"));
            Assert.That(cut.Markup, Does.Not.Contain("builtin:web_search"));
            Assert.That(cut.Markup, Does.Not.Contain("{\\"));
            // 1 spinner (call sem resultado) + 1 check (concluída).
            Assert.That(cut.FindAll("svg.animate-spin"), Has.Count.EqualTo(1));
            Assert.That(cut.Find("div[role='log']"), Is.Not.Null);
        });
    }

    [Test]
    public void Feed_Collapsed_MostraNPassos_EExpande()
    {
        using var ctx = Setup();
        var calls = new List<ToolCallView>
        {
            Live("1", "builtin:web_search", null, ok: true),
            Live("2", "builtin:file_edit", null, ok: true),
            Live("3", "builtin:shell_exec", null, ok: false, denied: true),
        };
        var cut = ctx.Render<RunActivityFeed>(p => p
            .Add(c => c.Calls, calls)
            .Add(c => c.Streaming, false)
            .Add(c => c.Collapsed, true));

        // Colapsado: só o resumo; marca âmbar do denied.
        Assert.That(cut.Markup, Does.Contain("chat.worklog.steps"));
        Assert.That(cut.Markup, Does.Not.Contain("chat.activity.web_search"));
        Assert.That(cut.FindAll("span.bg-amber-500"), Has.Count.EqualTo(1));

        cut.Find("button[aria-expanded]").Click();
        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("chat.activity.web_search"));
            Assert.That(cut.Markup, Does.Contain("chat.activity.exec"));
        });
    }

    [Test]
    public void Feed_LinhaExpande_DetalhesDoCard()
    {
        using var ctx = Setup();
        var calls = new List<ToolCallView>
        {
            Live("1", "builtin:file_edit", "{\"path\":\"a.cs\"}", ok: true),
        };
        var cut = ctx.Render<RunActivityFeed>(p => p
            .Add(c => c.Calls, calls)
            .Add(c => c.Streaming, false)
            .Add(c => c.Collapsed, false));

        // Nome da tool só aparece dentro dos detalhes (clique na linha).
        Assert.That(cut.Markup, Does.Not.Contain("builtin:file_edit"));
        cut.Find("button[aria-expanded]").Click();
        Assert.That(cut.Markup, Does.Contain("builtin:file_edit"));
        // segundo clique recolhe
        cut.Find("button[aria-expanded]").Click();
        Assert.That(cut.Markup, Does.Not.Contain("builtin:file_edit"));
    }
}
