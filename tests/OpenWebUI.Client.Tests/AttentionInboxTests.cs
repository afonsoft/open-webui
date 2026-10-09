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
/// SPEC-20261009-attention-inbox (D2): o badge de "Aguardando" mostra a
/// contagem do endpoint, esconde quando zera e ativa o filtro dedicado da
/// sidebar, que lista só os chats marcados com <c>awaiting</c>.
/// </summary>
[TestFixture]
public sealed class AttentionInboxTests
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

    private static object ChatResumo(string id, string title, bool awaiting) => new
    {
        id,
        title,
        pinned = false,
        folderId = (string?)null,
        tags = Array.Empty<string>(),
        createdAt = 1L,
        updatedAt = 1L,
        awaiting,
    };

    private static readonly Dictionary<string, string> Dict = new()
    {
        ["sidebar.attention"] = "Aguardando você",
        ["sidebar.attention_aria"] = "{{count}} chats aguardando sua ação",
        ["sidebar.attention_empty"] = "Nenhum chat aguardando você",
        ["sidebar.attention_item"] = "Aguardando sua ação",
    };

    private static HttpResponseMessage Route(HttpRequestMessage req, int count)
    {
        var path = req.RequestUri?.AbsolutePath ?? "";
        return path switch
        {
            "/api/v1/chats/" => Json(new object[]
            {
                ChatResumo("c1", "Chat aguardando", awaiting: true),
                ChatResumo("c2", "Chat normal", awaiting: false),
            }),
            "/api/v1/chats/pinned" => Json(Array.Empty<object>()),
            "/api/v1/chats/attention/count" => Json(new { count }),
            "/api/v1/folders/" => Json(Array.Empty<object>()),
            "/api/v1/channels" => Json(Array.Empty<object>()),
            "/i18n/pt-BR.json" or "/i18n/en-US.json" => Json(Dict),
            "/i18n/locales.json" => Json(new[] { new { code = "pt-BR", name = "Português" } }),
            _ => Json(Array.Empty<object>()),
        };
    }

    private static async Task<(BunitContext Ctx, FakeHandler Handler, AttentionInboxState Attention)>
        SetupAsync(int count)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var handler = new FakeHandler(req => Route(req, count));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var l10n = new LocalizationService(http, ctx.JSInterop.JSRuntime);
        await l10n.InitializeAsync();
        var auth = new AuthService(http,
            new BrowserStorage(ctx.JSInterop.JSRuntime), l10n);
        var api = new ApiService(http, auth);
        var attention = new AttentionInboxState(api);
        ctx.Services.AddSingleton(api);
        ctx.Services.AddSingleton(l10n);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(new DialogService());
        ctx.Services.AddSingleton(new ThemeService(
            ctx.JSInterop.JSRuntime, new BrowserStorage(ctx.JSInterop.JSRuntime)));
        ctx.Services.AddSingleton(new ChatListState());
        ctx.Services.AddSingleton(attention);
        return (ctx, handler, attention);
    }

    [Test]
    public async Task Badge_CountPositivo_RenderizaContagem()
    {
        var (ctx, _, _) = await SetupAsync(count: 2);
        await using var _ = ctx.Services.GetRequiredService<AttentionInboxState>()
            .ConfigureAwait(false);
        using var cut = ctx.Render<Sidebar>();

        cut.WaitForAssertion(() =>
        {
            var badge = cut.Find("button[aria-pressed]");
            Assert.That(badge.TextContent, Does.Contain("2"));
            Assert.That(badge.GetAttribute("aria-label"),
                Does.Contain("2 chats aguardando"));
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Badge_CountZero_Escondido()
    {
        var (ctx, _, _) = await SetupAsync(count: 0);
        await using var _ = ctx.Services.GetRequiredService<AttentionInboxState>();
        using var cut = ctx.Render<Sidebar>();

        await Task.Delay(300);
        Assert.That(cut.FindAll("button[aria-pressed]"), Is.Empty,
            "sem chats aguardando o badge não renderiza");
    }

    [Test]
    public async Task Badge_Clique_AtivaFiltroAguardando()
    {
        var (ctx, _, attention) = await SetupAsync(count: 1);
        await using var _ = attention;
        using var cut = ctx.Render<Sidebar>();

        cut.WaitForAssertion(() => Assert.That(
            cut.FindAll("button[aria-pressed]"), Has.Count.EqualTo(1)),
            TimeSpan.FromSeconds(5));

        cut.Find("button[aria-pressed]").Click();

        Assert.That(attention.FilterActive, Is.True);
        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Aguardando você"));
            Assert.That(cut.Markup, Does.Contain("Chat aguardando"));
            Assert.That(cut.Markup, Does.Not.Contain("Chat normal"),
                "filtro ativo lista apenas chats aguardando");
        }, TimeSpan.FromSeconds(5));
    }
}
