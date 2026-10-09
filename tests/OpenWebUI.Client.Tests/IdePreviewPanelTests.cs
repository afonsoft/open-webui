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
/// RF-003 (SPEC-20261009-port-preview, E16 D4): aba Preview do /ide —
/// input de porta, probe no proxy, iframe sandboxed com src
/// /preview/{port}/, erro em 502 e estado desativado pela flag.
/// </summary>
[TestFixture]
public sealed class IdePreviewPanelTests
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

    private static HttpResponseMessage Status(HttpStatusCode code) =>
        new(code) { Content = new StringContent("") };

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

    private static Bunit.BunitContext EnabledCtx(
        Func<HttpRequestMessage, HttpResponseMessage>? previewRoute = null) =>
        Setup(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/v1/preview/config" => Json(new { enabled = true }),
            var p when p.StartsWith("/preview/", StringComparison.Ordinal) =>
                previewRoute?.Invoke(req) ?? Status(HttpStatusCode.OK),
            _ => Status(HttpStatusCode.NotFound),
        });

    [Test]
    public void Panel_PortaValida_IframeComSrcESandbox()
    {
        using var ctx = EnabledCtx();
        var cut = ctx.Render<IdePreviewPanel>();
        cut.WaitForAssertion(() => Assert.That(cut.Find("input"), Is.Not.Null));

        cut.Find("input").Input("3000");
        cut.Find("button").Click(); // Open

        cut.WaitForAssertion(() =>
        {
            var iframe = cut.Find("iframe");
            Assert.That(iframe.GetAttribute("src"), Is.EqualTo("/preview/3000/"));
            var sandbox = iframe.GetAttribute("sandbox")!;
            Assert.That(sandbox, Does.Contain("allow-scripts"));
            Assert.That(sandbox, Does.Contain("allow-same-origin"));
            Assert.That(sandbox, Does.Contain("allow-forms"));
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Panel_UpstreamRespondendo_ComErro_IframeAbreMesmoAssim()
    {
        // 404 do app proxied = algo escuta na porta — iframe abre (só 502/504 dão erro).
        using var ctx = EnabledCtx(_ => Status(HttpStatusCode.NotFound));
        var cut = ctx.Render<IdePreviewPanel>();
        cut.WaitForAssertion(() => Assert.That(cut.Find("input"), Is.Not.Null));

        cut.Find("input").Input("3000");
        cut.Find("button").Click();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Find("iframe").GetAttribute("src"),
                Is.EqualTo("/preview/3000/")), TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Panel_502_MostraErroSemIframe()
    {
        using var ctx = EnabledCtx(_ => Status(HttpStatusCode.BadGateway));
        var cut = ctx.Render<IdePreviewPanel>();
        cut.WaitForAssertion(() => Assert.That(cut.Find("input"), Is.Not.Null));

        cut.Find("input").Input("9999");
        cut.Find("button").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("ide.preview_error"));
            Assert.That(cut.FindAll("iframe"), Is.Empty);
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Panel_PortaInvalida_MostraErroSemProbe()
    {
        var probed = false;
        using var ctx = EnabledCtx(_ =>
        {
            probed = true;
            return Status(HttpStatusCode.OK);
        });
        var cut = ctx.Render<IdePreviewPanel>();
        cut.WaitForAssertion(() => Assert.That(cut.Find("input"), Is.Not.Null));

        cut.Find("input").Input("80");
        cut.Find("button").Click();

        Assert.That(cut.Markup, Does.Contain("ide.preview_invalid"));
        Assert.That(probed, Is.False);
        Assert.That(cut.FindAll("iframe"), Is.Empty);
    }

    [Test]
    public void Panel_FlagOff_MostraDisabled()
    {
        using var ctx = Setup(_ => Json(new { enabled = false }));
        var cut = ctx.Render<IdePreviewPanel>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("ide.preview_disabled")),
            TimeSpan.FromSeconds(5));
    }
}
