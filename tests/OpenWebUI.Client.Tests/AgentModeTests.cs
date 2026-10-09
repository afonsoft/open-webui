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
/// SPEC-20261009-agent-modes-plan-build RF-004: o toggle do composer
/// renderiza com rótulo/ícone e <c>aria-pressed</c> coerente ao modo e o
/// chip do header mostra o modo (badge "Modo plano" no plan).
/// </summary>
[TestFixture]
public sealed class AgentModeTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
    }

    private static Bunit.BunitContext Setup()
    {
        var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new FakeHandler())
        {
            BaseAddress = new Uri("http://localhost/"),
        };
        ctx.Services.AddSingleton(new LocalizationService(http, ctx.JSInterop.JSRuntime));
        return ctx;
    }

    [Test]
    public void Toggle_Plan_RenderizaComAriaPressed()
    {
        var ctx = Setup();
        using var cut = ctx.Render<AgentModeToggle>(p => p.Add(c => c.Mode, "plan"));
        var button = cut.Find("button");
        Assert.Multiple(() =>
        {
            Assert.That(button.GetAttribute("aria-pressed"), Is.EqualTo("true"));
            Assert.That(button.TextContent, Does.Contain("chat.mode_plan"));
            Assert.That(button.GetAttribute("class"), Does.Contain("violet"));
        });
    }

    [Test]
    public void Toggle_Build_NaoPressionado()
    {
        var ctx = Setup();
        using var cut = ctx.Render<AgentModeToggle>(p => p.Add(c => c.Mode, "build"));
        var button = cut.Find("button");
        Assert.Multiple(() =>
        {
            Assert.That(button.GetAttribute("aria-pressed"), Is.EqualTo("false"));
            Assert.That(button.TextContent, Does.Contain("chat.mode_build"));
            Assert.That(button.GetAttribute("class"), Does.Not.Contain("violet"));
        });
    }

    [Test]
    public void Toggle_Clique_DisparaCallback()
    {
        var ctx = Setup();
        var clicou = false;
        using var cut = ctx.Render<AgentModeToggle>(p => p
            .Add(c => c.Mode, "plan")
            .Add(c => c.OnToggle, () => clicou = true));
        cut.Find("button").Click();
        Assert.That(clicou, Is.True);
    }

    [Test]
    public void Chip_Plan_MostraModo()
    {
        var ctx = Setup();
        using var cut = ctx.Render<AgentModeBadge>(p => p.Add(c => c.Mode, "plan"));
        var chip = cut.Find("[role=status]");
        Assert.Multiple(() =>
        {
            Assert.That(chip.TextContent, Does.Contain("chat.mode_plan"), "chip mostra 'Modo plano'");
            Assert.That(chip.GetAttribute("class"), Does.Contain("violet"));
        });
    }

    [Test]
    public void Chip_Build_MostraBuild()
    {
        var ctx = Setup();
        using var cut = ctx.Render<AgentModeBadge>(p => p.Add(c => c.Mode, "build"));
        var chip = cut.Find("[role=status]");
        Assert.Multiple(() =>
        {
            Assert.That(chip.TextContent, Does.Contain("chat.mode_build"));
            Assert.That(chip.GetAttribute("class"), Does.Not.Contain("violet"));
        });
    }

    [Test]
    public void Normalize_Desconhecido_ViraBuild()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AgentModeVisuals.Normalize("plan"), Is.EqualTo("plan"));
            Assert.That(AgentModeVisuals.Normalize("build"), Is.EqualTo("build"));
            Assert.That(AgentModeVisuals.Normalize("yolo"), Is.EqualTo("build"));
            Assert.That(AgentModeVisuals.Normalize(null), Is.EqualTo("build"));
        });
    }
}
