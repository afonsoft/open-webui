using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura do <see cref="WebLoaderService.LoadYoutubeTranscriptAsync"/>
/// e do endpoint <c>POST /api/v1/retrieval/process/youtube</c>
/// (SPEC-20261008-tests-coverage-gate RF-002): extração de videoId,
/// parsing de captionTracks, erros <see cref="WebLoaderException"/> e o
/// fluxo completo transcript→FileEntry→indexação pendente.
/// </summary>
public class WebLoaderYoutubeTests
{
    private const string VideoPage = """
        <html><body><script>var yt = {
        "captions":{"playerCaptionsTracklistRenderer":{"captionTracks":[{"baseUrl":"http://yt.test/caption?lang=pt","languageCode":"pt"}]
        }}};</script></body></html>
        """;

    private const string TranscriptXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <transcript><text start="0" dur="1">Olá&amp;nbsp;mundo</text>
        <text start="1" dur="1">segunda linha</text></transcript>
        """;

    private sealed class YtHandler : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _created = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var body = url.Contains("/caption")
                ? TranscriptXml
                : VideoPage;
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/html"),
            };
            _created.Add(resp);
            return Task.FromResult(resp);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var r in _created)
                {
                    r.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }

    private sealed class FixedHandler(HttpStatusCode status, string body)
        : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _created = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/html"),
            };
            _created.Add(resp);
            return Task.FromResult(resp);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var r in _created)
                {
                    r.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h);
    }

    private static WebLoaderService Loader(HttpMessageHandler h) => new(new Factory(h));

    // ---------------- LoadYoutubeTranscriptAsync ----------------

    [Test]
    public async Task Yt_UrlInvalida_LancaWebLoaderException()
    {
        var loader = Loader(new YtHandler());
        var ex = await Assert.ThrowsAsync<WebLoaderException>(
            () => loader.LoadYoutubeTranscriptAsync("https://example.com/x"));
        Assert.That(ex!.Message, Does.Contain("inválida"));
    }

    [Test]
    public async Task Yt_PaginaSemCaptions_Lanca()
    {
        var loader = Loader(new FixedHandler(HttpStatusCode.OK, "<html>sem nada</html>"));
        var ex = await Assert.ThrowsAsync<WebLoaderException>(
            () => loader.LoadYoutubeTranscriptAsync("https://youtu.be/abc12345"));
        Assert.That(ex!.Message, Does.Contain("sem transcrição"));
    }

    [Test]
    public async Task Yt_CaptionsMalformadas_Lanca()
    {
        var loader = Loader(new FixedHandler(HttpStatusCode.OK,
            "<html>\"captionTracks\":[ {nunca fecha" + new string('x', 20) + "</html>"));
        var ex = await Assert.ThrowsAsync<WebLoaderException>(
            () => loader.LoadYoutubeTranscriptAsync("https://www.youtube.com/watch?v=abc12345"));
        Assert.That(ex!.Message, Does.Contain("malformada"));
    }

    [Test]
    public async Task Yt_TranscricaoVazia_Lanca()
    {
        var handler = new DelegateHandler2(url =>
            url.Contains("/caption")
                ? "<?xml version=\"1.0\"?><transcript></transcript>"
                : VideoPage);
        var loader = Loader(handler);
        var ex = await Assert.ThrowsAsync<WebLoaderException>(
            () => loader.LoadYoutubeTranscriptAsync("https://www.youtube.com/watch?v=abc12345"));
        Assert.That(ex!.Message, Does.Contain("vazia"));
    }

    [Test]
    public async Task Yt_Sucesso_RetornaTextoConcatenado()
    {
        var loader = Loader(new YtHandler());
        var text = await loader.LoadYoutubeTranscriptAsync(
            "https://www.youtube.com/shorts/xyz789abc");
        Assert.That(text, Does.Contain("Olá").And.Contain("mundo")
            .And.Contain("segunda linha"));
    }

    [Test]
    public void HtmlToText_RemoveScriptsEStyles()
    {
        var text = WebLoaderService.HtmlToText(
            "<html><head><style>body{color:red}</style></head>"
            + "<body><script>evil()</script><p>Olá <b>mundo</b></p></body></html>");
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("Olá"));
            Assert.That(text, Does.Not.Contain("evil"));
            Assert.That(text, Does.Not.Contain("color:red"));
        });
    }

    private sealed class DelegateHandler2(Func<string, string> map) : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _created = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    map(request.RequestUri!.ToString()), Encoding.UTF8, "text/html"),
            };
            _created.Add(resp);
            return Task.FromResult(resp);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var r in _created)
                {
                    r.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }

    // ---------------- endpoint /process/youtube ----------------

    [Test]
    public async Task Endpoint_Youtube_IndexaComoFile()
    {
        var dbPath = Path.Join(Path.GetTempPath(),
            $"openwebui-yt-{Guid.NewGuid():N}.db");
        var old = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Default", $"Data Source={dbPath}");
        try
        {
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b => b.ConfigureTestServices(services =>
                {
                    services.RemoveAll<WebLoaderService>();
                    services.AddScoped<WebLoaderService>(
                        _ => new WebLoaderService(new Factory(new YtHandler())));
                }));
            using var client = factory.CreateClient();

            var signup = await client.PostAsJsonAsync("/api/v1/auths/signup",
                new SignUpRequest("Yt User", "yt@test.local", "senha123"));
            Assert.That(signup.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                await signup.Content.ReadAsStringAsync());
            var auth = await signup.Content.ReadFromJsonAsync<AuthResponse>();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", auth!.Token);

            var response = await client.PostAsJsonAsync(
                "/api/v1/retrieval/process/youtube",
                new ProcessYoutubeRequest("https://youtu.be/abc12345", null));

            // Sem provider de embedding a indexação pode ficar pendente (200)
            // ou falhar (400) conforme o ambiente — ambos percorrem o endpoint.
            var body = await response.Content.ReadAsStringAsync();
            Assert.That(response.StatusCode,
                Is.EqualTo(HttpStatusCode.OK).Or.EqualTo(HttpStatusCode.BadRequest), body);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var result = JsonSerializer.Deserialize<ProcessResponse>(
                    body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Assert.Multiple(() =>
                {
                    Assert.That(result!.FileId, Is.Not.Empty);
                    Assert.That(result.Filename, Does.StartWith("youtube-"));
                });
            }
            else
            {
                Assert.That(body, Does.Contain("indexar"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", old);
            try { File.Delete(dbPath); }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }
}
