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
/// SPEC-20261009-notification-feed D3: badge de não-lidas no sino, dropdown
/// com a lista do feed, clique que marca lido e "marcar todas".
/// </summary>
[TestFixture]
public sealed class NotificationBellTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> route)
        : HttpMessageHandler
    {
        public List<(string Method, string Path)> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.Method.Method, request.RequestUri?.PathAndQuery ?? ""));
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

    private static object Page(int unread, params object[] items) => new
    {
        items,
        total = items.Length,
        unread,
        page = 1,
    };

    private static object Item(string id, string title, bool read = false, string? link = "/c/abc") =>
        new
        {
            id,
            kind = "run.completed",
            title,
            body = $"body {title}",
            link,
            readAt = read ? 1700000001L : (long?)null,
            createdAt = 1700000000L,
        };

    private sealed class Fixture : IDisposable
    {
        public required Bunit.BunitContext Ctx { get; init; }
        public required FakeHandler Handler { get; init; }
        public required HttpClient Http { get; init; }

        public void Dispose()
        {
            Ctx.Dispose();
            Http.Dispose();
        }
    }

    private static Fixture Setup(Func<HttpRequestMessage, HttpResponseMessage> route)
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
        return new Fixture { Ctx = ctx, Handler = handler, Http = http };
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Feed(object page) =>
        request => request.RequestUri?.AbsolutePath.StartsWith("/api/v1/notifications") == true
            ? Json(request.Method == HttpMethod.Get ? page : new { ok = true })
            : Json(new { });

    [Test]
    public void Badge_MostraContagemNaoLidas()
    {
        using var fx = Setup(Feed(Page(3, Item("1", "a"), Item("2", "b"), Item("3", "c"))));
        using var cut = fx.Ctx.Render<NotificationBell>();
        cut.WaitForAssertion(() =>
        {
            var badge = cut.Find("[aria-label*='notifications.unread_aria']");
            Assert.That(badge.TextContent.Trim(), Is.EqualTo("3"));
        });
    }

    [Test]
    public void Badge_Zero_NaoRenderiza()
    {
        using var fx = Setup(Feed(Page(0)));
        using var cut = fx.Ctx.Render<NotificationBell>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("[aria-label*='notifications.unread_aria']"), Is.Empty));
    }

    [Test]
    public void Dropdown_AbreComItens()
    {
        using var fx = Setup(Feed(Page(2,
            Item("1", "run ok"), Item("2", "run 2", read: true))));
        using var cut = fx.Ctx.Render<NotificationBell>();
        cut.Find("button").Click();
        cut.WaitForAssertion(() =>
        {
            var menu = cut.Find("[role=menu]");
            Assert.That(menu.TextContent, Does.Contain("run ok").And.Contain("run 2"));
        });
        // Item lido não tem o ponto de não-lida; não-lido tem.
        Assert.That(cut.FindAll(".bg-blue-500"), Has.Count.EqualTo(1));
    }

    [Test]
    public void Item_Clique_MarcaLidoENavega()
    {
        using var fx = Setup(Feed(Page(1, Item("n1", "alvo"))));
        using var cut = fx.Ctx.Render<NotificationBell>();
        cut.Find("button").Click();
        cut.WaitForAssertion(() => Assert.That(cut.FindAll("[role=menuitem]"), Has.Count.EqualTo(1)));

        cut.Find("[role=menuitem]").Click();
        cut.WaitForAssertion(() =>
            Assert.That(fx.Handler.Calls.Any(c =>
                c.Method == "POST" && c.Path == "/api/v1/notifications/n1/read"), Is.True,
                "esperava POST /{id}/read"));

        var nav = fx.Ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        Assert.That(nav.Uri, Does.EndWith("/c/abc"));
    }

    [Test]
    public void MarkAll_ZeraBadge()
    {
        using var fx = Setup(Feed(Page(2, Item("1", "a"), Item("2", "b"))));
        using var cut = fx.Ctx.Render<NotificationBell>();
        cut.Find("button").Click();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("button")
                    .Any(b => b.TextContent.Contains("notifications.mark_all")), Is.True));

        cut.FindAll("button")
            .First(b => b.TextContent.Contains("notifications.mark_all"))
            .Click();
        cut.WaitForAssertion(() =>
        {
            Assert.That(fx.Handler.Calls.Any(c =>
                c.Method == "POST" && c.Path == "/api/v1/notifications/read-all"), Is.True);
            Assert.That(cut.FindAll("[aria-label*='notifications.unread_aria']"), Is.Empty,
                "badge some após read-all");
        });
    }
}
