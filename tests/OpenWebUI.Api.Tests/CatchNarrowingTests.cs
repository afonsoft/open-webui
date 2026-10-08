using System.Net;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.Services.Image;
using OpenWebUI.Infrastructure.Services.Video;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobre os catches tipados introduzidos pelo slice E15-s2 (boundary catches
/// estreitados de <c>catch (Exception)</c> para tipos concretos).
/// </summary>
[TestFixture]
public class CatchNarrowingTests
{
    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(ex);
    }

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private static IEnumerable<(string Name, Func<IHttpClientFactory, object> Ctor)> ImageEngines() =>
    [
        ("a1111", f => new A1111Engine(f)),
        ("comfyui", f => new ComfyUiEngine(f)),
        ("gemini", f => new GeminiImageEngine(f)),
        ("openai", f => new OpenAiImageEngine(f)),
    ];

    [Test]
    public async Task Engine_TestAsync_FalhaDeRede_RetornaFalse()
    {
        var cfg = new ImagesConfig(true, "x", "http://engine.local", "", "m", "1024x1024", 30);
        var exceptions = new Exception[]
        {
            new HttpRequestException("sem rota"),
            new JsonException("json quebrado"),
            new InvalidOperationException("request inválido"),
            new TaskCanceledException("timeout"),
        };
        foreach (var (name, ctor) in ImageEngines())
        {
            foreach (var ex in exceptions)
            {
                var engine = (ImageEngineBase)ctor(new StubHttpClientFactory(new ThrowingHandler(ex)));
                var (ok, detail) = await engine.TestAsync(cfg, CancellationToken.None);
                Assert.That(ok, Is.False, $"{name}/{ex.GetType().Name}");
                Assert.That(detail, Is.EqualTo(ex.Message), $"{name}/{ex.GetType().Name}");
            }
        }
    }

    [Test]
    public async Task VideoEngine_TestAsync_FalhaDeRede_RetornaFalse()
    {
        var cfg = new ImagesConfig(true, "x", "http://engine.local", "", "m", "720p", 30);
        var engines = new Func<IHttpClientFactory, object>[]
        {
            f => new OpenAiVideoEngine(f),
            f => new ComfyUiVideoEngine(f),
        };
        var exceptions = new Exception[]
        {
            new HttpRequestException("sem rota"),
            new JsonException("json quebrado"),
            new InvalidOperationException("request inválido"),
            new TaskCanceledException("timeout"),
        };
        foreach (var ctor in engines)
        {
            foreach (var ex in exceptions)
            {
                var engine = ctor(new StubHttpClientFactory(new ThrowingHandler(ex)));
                var (ok, detail) = engine switch
                {
                    OpenAiVideoEngine e => await e.TestAsync(cfg, CancellationToken.None),
                    ComfyUiVideoEngine e => await e.TestAsync(cfg, CancellationToken.None),
                    _ => throw new InvalidOperationException("engine inesperada"),
                };
                Assert.That(ok, Is.False);
                Assert.That(detail, Is.EqualTo(ex.Message));
            }
        }
    }

    [Test]
    public async Task GitWorkdir_Inexistente_GetInfo_ErroTipado()
    {
        var svc = new WorkspaceGitService();
        // WorkingDirectory inexistente → Process.Start lança Win32Exception → null.
        var info = await svc.GetInfoAsync("/nonexistent-dir-xyz-123", CancellationToken.None);
        Assert.That(info, Is.Not.Null);
    }

    [Test]
    public void RiskClassifier_ArgInvalido_NaoExplode()
    {
        // '\0' no argumento → Path.GetFullPath lança ArgumentException → trata como fora do workspace.
        var assessment = CommandRiskClassifier.Classify("cat \0arquivo", Path.GetTempPath());
        Assert.That(assessment, Is.Not.Null);
    }

    [Test]
    public void RiskClassifier_ArgMuitoLongo_NaoExplode()
    {
        // argumento > MAX_PATH → PathTooLongException → trata como fora do workspace.
        var longo = new string('a', 33_000);
        var assessment = CommandRiskClassifier.Classify($"cat /tmp/{longo}", Path.GetTempPath());
        Assert.That(assessment, Is.Not.Null);
    }

    [Test]
    public async Task FetchUrl_CharsetDesconhecido_CaiNoUtf8()
    {
        var html = "<html><head><meta charset=\"bogus-enc-99\"></head><body>ok</body></html>";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        };
        var tool = new FetchUrlBuiltinTool(new StubHttpClientFactory(new StaticHandler(response)));
        var args = JsonDocument.Parse("{\"url\":\"https://example.com/p\"}").RootElement;
        var result = await tool.ExecuteAsync(
            args, new BuiltinToolContext("u", null, null, "/tmp", "/tmp"),
            CancellationToken.None);
        Assert.That(result.Text, Does.Not.Contain("inválida"));
    }
}
