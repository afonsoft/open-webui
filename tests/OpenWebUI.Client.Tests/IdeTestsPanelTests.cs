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
/// RF-004 (SPEC-20261009-ide-mentions-tests): aba Tests — cartão de
/// aprovação com o comando visível (WorkspaceWrite), spinner durante o
/// run, badge ✓/✗ + contagens + log ao final.
/// </summary>
[TestFixture]
public sealed class IdeTestsPanelTests
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
        ctx.Services.AddSingleton(new IdeTestRunService(new ApiService(http, auth)));
        return ctx;
    }

    [Test]
    public void Panel_ComandoGated_MostraCartaoComComandoVisivel()
    {
        using var ctx = Setup(_ => Json(new
        {
            jobId = default(string),
            command = "npm test",
            requiresApproval = true,
            reason = "comando mutante confinado ao workspace",
        }));

        var cut = ctx.Render<IdeTestsPanel>();
        cut.Find("button").Click(); // Run tests

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("npm test"));
            Assert.That(cut.Markup, Does.Contain("tests_approve_title").Or.Contain("Approve"));
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Panel_RunCompleto_MostraBadgeContagensELog()
    {
        using var ctx = Setup(req =>
            req.Method == HttpMethod.Post
                ? Json(new { jobId = "job-1", command = "dotnet test", requiresApproval = false, reason = default(string) })
                : Json(new
                {
                    state = "completed",
                    summary = new { passed = 12, failed = 0, skipped = 1, durationMs = 2300L },
                    tail = "Passed! - Failed: 0, Passed: 12, Skipped: 1",
                    command = "dotnet test",
                    exitCode = 0,
                    error = default(string),
                }));

        var cut = ctx.Render<IdeTestsPanel>();
        cut.Find("button").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Find(".test-badge"), Is.Not.Null);
            Assert.That(cut.Markup, Does.Contain("12"));
            Assert.That(cut.Markup, Does.Contain("Passed!"));
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Panel_RunFalhou_MostraBadgeDeFalha()
    {
        using var ctx = Setup(req =>
            req.Method == HttpMethod.Post
                ? Json(new { jobId = "job-2", command = "dotnet test", requiresApproval = false, reason = default(string) })
                : Json(new
                {
                    state = "failed",
                    summary = new { passed = default(int?), failed = default(int?), skipped = default(int?), durationMs = 100L },
                    tail = "spawn failed",
                    command = "dotnet test",
                    exitCode = default(int?),
                    error = "Falha ao iniciar",
                }));

        var cut = ctx.Render<IdeTestsPanel>();
        cut.Find("button").Click();

        cut.WaitForAssertion(() =>
        {
            var badge = cut.Find(".test-badge");
            Assert.That(badge.TextContent, Does.Contain("✗"));
        }, TimeSpan.FromSeconds(5));
    }
}
