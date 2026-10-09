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
/// D1 (SPEC-20261009-pr-ci-panel): painel "Pull Requests" — lista com
/// chip de checks (✓/✗/pending) e empty states (sem repo, sem token,
/// zero PRs, GitHub indisponível).
/// </summary>
[TestFixture]
public sealed class WorkspacePullsPanelTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> route)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(route(request));
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(body,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            Encoding.UTF8, "application/json"),
    };

    private static Bunit.BunitContext Setup(
        Func<HttpRequestMessage, HttpResponseMessage> route)
    {
        var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new FakeHandler(route)) { BaseAddress = new Uri("http://localhost/") };
        ctx.Services.AddSingleton(http);
        var l10n = new LocalizationService(http, ctx.JSInterop.JSRuntime);
        var auth = new AuthService(http, new BrowserStorage(ctx.JSInterop.JSRuntime), l10n);
        ctx.Services.AddSingleton(l10n);
        ctx.Services.AddSingleton(new ApiService(http, auth));
        return ctx;
    }

    private static HttpResponseMessage Route(HttpRequestMessage req, object? pulls)
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path.EndsWith("/pulls", StringComparison.Ordinal))
        {
            return pulls is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json(pulls);
        }
        // GET /api/v1/workspace/repo/ → binding
        return Json(new { repo = "a/b", branch = "main", dir = "repos/a__b", testCommand = (string?)null });
    }

    [Test]
    public void Panel_ListaPRs_MostraTituloAutorEChips()
    {
        using var ctx = Setup(req => Route(req, new
        {
            github = true,
            needsToken = false,
            pulls = new object[]
            {
                new
                {
                    number = 7,
                    title = "feat: painel",
                    author = "afonso",
                    headRef = "devin/x",
                    updatedAt = "2026-10-09T10:00:00Z",
                    url = "https://github.com/a/b/pull/7",
                    draft = false,
                    checks = new { total = 5, passing = 2, failing = 1, pending = 2, state = "failure" },
                },
                new
                {
                    number = 8,
                    title = "fix: bug",
                    author = "bia",
                    headRef = "devin/y",
                    updatedAt = "2026-10-08T09:00:00Z",
                    url = "https://github.com/a/b/pull/8",
                    draft = true,
                    checks = new { total = 3, passing = 3, failing = 0, pending = 0, state = "success" },
                },
            },
        }));

        var cut = ctx.Render<WorkspacePullsPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("feat: painel"));
            Assert.That(cut.Markup, Does.Contain("#7"));
            Assert.That(cut.Markup, Does.Contain("devin/x"));
            Assert.That(cut.Markup, Does.Contain("@afonso"));
            var chips = cut.FindAll(".pull-checks");
            Assert.That(chips, Has.Count.EqualTo(2));
            Assert.That(chips[0].TextContent, Does.Contain("✗1"));
            Assert.That(chips[1].TextContent, Does.Contain("✓3"));
        }, TimeSpan.FromSeconds(5));

        var link = cut.Find("a[href]");
        Assert.That(link.GetAttribute("target"), Is.EqualTo("_blank"));
        Assert.That(link.GetAttribute("href"), Does.Contain("/pull/7"));
    }

    [Test]
    public void Panel_SemRepo_MostraEmptyState()
    {
        using var ctx = Setup(req => Json(new
        {
            repo = (string?)null, branch = (string?)null,
            dir = (string?)null, testCommand = (string?)null,
        }));

        var cut = ctx.Render<WorkspacePullsPanel>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("chat.pulls_no_repo")),
            TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Panel_NeedsToken_MostraHint()
    {
        using var ctx = Setup(req => Route(req, new
        {
            github = false, needsToken = true, pulls = Array.Empty<object>(),
        }));

        var cut = ctx.Render<WorkspacePullsPanel>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("chat.pulls_needs_token")),
            TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Panel_ZeroPRs_MostraEmpty()
    {
        using var ctx = Setup(req => Route(req, new
        {
            github = true, needsToken = false, pulls = Array.Empty<object>(),
        }));

        var cut = ctx.Render<WorkspacePullsPanel>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("chat.pulls_empty")),
            TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Panel_Pulls404_MostraIndisponivel()
    {
        using var ctx = Setup(req => Route(req, null));

        var cut = ctx.Render<WorkspacePullsPanel>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("chat.pulls_unavailable")),
            TimeSpan.FromSeconds(5));
    }
}
