using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Bordas do <see cref="ProviderCapabilityService"/> (SPEC-20261008-
/// tests-coverage-gate RF-002): detecção por conexão (openai /models,
/// ollama /v1→/api/tags fallback), mapa persistido por conexão e
/// agregação com união de candidatos.
/// </summary>
public class ProviderCapabilityEdgeTests
{
    private AppDbContext _db = null!;
    private ConfigService _config = null!;
    private RoutingHandler _handler = null!;
    private ProviderCapabilityService _svc = null!;

    [SetUp]
    public void SetUp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"owui-cap-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path}").Options);
        DatabaseMigrator.MigrateAsync(_db).GetAwaiter().GetResult();
        _config = new ConfigService(_db, new MemoryCache(new MemoryCacheOptions()));
        _handler = new RoutingHandler();
        _svc = new ProviderCapabilityService(new StubFactory(_handler), _config,
            NullLogger<ProviderCapabilityService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _handler.Dispose();
    }

    private Task SetConnections(string[] ollama, string[] openai, string[] keys) =>
        _config.SetAsync("connections", new ConnectionsConfig(
            OllamaBaseUrls: ollama, OpenAiBaseUrls: openai, OpenAiApiKeys: keys), default);

    // ---------------- DetectConnectionAsync ----------------

    [Test]
    public async Task Detect_IndiceFora_RetornaNull()
    {
        await SetConnections([], ["http://ai.test"], ["k"]);
        Assert.That(await _svc.DetectConnectionAsync("openai", 5), Is.Null);
        Assert.That(await _svc.DetectConnectionAsync("openai", -1), Is.Null);
    }

    [Test]
    public async Task Detect_OpenAi_CatalogoEPersisteMapa()
    {
        await SetConnections([], ["http://ai.test"], ["sk-x"]);
        _handler.Respond(HttpStatusCode.OK, new StringContent(
            "{\"data\":["
            + "{\"id\":\"gpt-image-1\",\"output_modalities\":[\"image\"]},"
            + "{\"id\":\"whisper-1\",\"input_modalities\":[\"audio\"]},"
            + "{\"id\":\"tts-1\",\"output_modalities\":[\"audio\"]}"
            + "]}", Encoding.UTF8, "application/json"));

        var d = await _svc.DetectConnectionAsync("openai", 0);

        Assert.Multiple(async () =>
        {
            Assert.That(d, Is.Not.Null);
            Assert.That(d!.Image, Does.Contain("gpt-image-1"));
            Assert.That(d.Stt, Does.Contain("whisper-1"));
            Assert.That(d.Tts, Does.Contain("tts-1"));
            Assert.That(d.BaseUrl, Is.EqualTo("http://ai.test"));
            Assert.That(_handler.LastRequest!.RequestUri!.ToString(),
                Is.EqualTo("http://ai.test/models"));
            Assert.That(_handler.LastRequest.Headers.Authorization!.Parameter,
                Is.EqualTo("sk-x"));
            // Mapa persistido com a chave "openai:0".
            var map = await _svc.GetConnectionMapAsync();
            Assert.That(map.ContainsKey("openai:0"), Is.True);
        });
    }

    [Test]
    public async Task Detect_Ollama_CaiNoApiTags()
    {
        await SetConnections(["http://ollama.test"], [], []);
        // /v1/models 404 → fallback para /api/tags nativo.
        _handler.Route("/v1/models", HttpStatusCode.NotFound, "not found");
        _handler.Route("/api/tags", HttpStatusCode.OK,
            "{\"models\":[{\"name\":\"llama3.2\"},{\"name\":\"nomic-embed-text\"}]}");

        var d = await _svc.DetectConnectionAsync("ollama", 0);

        Assert.Multiple(() =>
        {
            Assert.That(d, Is.Not.Null);
            Assert.That(d!.Embed, Does.Contain("nomic-embed-text"));
            Assert.That(d.BaseUrl, Is.EqualTo("http://ollama.test"));
        });
    }

    [Test]
    public async Task Detect_ConexaoMorta_RemoveDoMapa()
    {
        await SetConnections([], ["http://ai.test"], ["k"]);
        _handler.Respond(HttpStatusCode.OK, new StringContent(
            "{\"data\":[{\"id\":\"tts-1\",\"output_modalities\":[\"audio\"]}]}",
            Encoding.UTF8, "application/json"));
        Assert.That(await _svc.DetectConnectionAsync("openai", 0), Is.Not.Null);

        // Falhou depois → detectado null + chave removida do mapa.
        _handler.Respond(HttpStatusCode.InternalServerError, new StringContent("x"));
        Assert.That(await _svc.DetectConnectionAsync("openai", 0), Is.Null);
        var map = await _svc.GetConnectionMapAsync();
        Assert.That(map.ContainsKey("openai:0"), Is.False);
    }

    // ---------------- GetAggregatedAsync ----------------

    [Test]
    public async Task Aggregate_Vazio_RetornaNull()
    {
        Assert.That(await _svc.GetAggregatedAsync(), Is.Null);
    }

    [Test]
    public async Task Aggregate_UniaoDasConexoes()
    {
        await _config.SetAsync(
            ProviderCapabilityService.DetectedConnectionsKey,
            new Dictionary<string, DetectedCapabilities>
            {
                ["openai:0"] = new(["img-a"], [], ["tts-a"], [], ["emb-a"],
                    "http://a.test", 100),
                ["ollama:0"] = new(["img-b"], ["vid-b"], [], ["stt-b"], ["emb-a", "emb-b"],
                    "http://b.test", 200),
            }, default);

        var agg = await _svc.GetAggregatedAsync();

        Assert.Multiple(() =>
        {
            Assert.That(agg, Is.Not.Null);
            Assert.That(agg!.Image, Is.EquivalentTo(new[] { "img-a", "img-b" }));
            Assert.That(agg.Video, Is.EqualTo(new[] { "vid-b" }));
            Assert.That(agg.Stt, Is.EqualTo(new[] { "stt-b" }));
            Assert.That(agg.Embed, Is.EquivalentTo(new[] { "emb-a", "emb-b" }));
            Assert.That(agg.DetectedAt, Is.EqualTo(200));
        });
    }

    [Test]
    public void ConnectionKey_Formato()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ProviderCapabilityService.ConnectionKey("openai", 2),
                Is.EqualTo("openai:2"));
            Assert.That(ProviderCapabilityService.ConnectionKey("ollama", 0),
                Is.EqualTo("ollama:0"));
        });
    }

    // ---------------- helpers ----------------

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "{}";
        private readonly Dictionary<string, (HttpStatusCode, string)> _routes = new();

        public HttpRequestMessage? LastRequest { get; private set; }

        public void Respond(HttpStatusCode status, HttpContent content)
        {
            _status = status;
            _body = content.ReadAsStringAsync().GetAwaiter().GetResult();
            _routes.Clear();
        }

        public void Route(string pathSuffix, HttpStatusCode status, string body) =>
            _routes[pathSuffix] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var url = request.RequestUri!.ToString();
            foreach (var (suffix, (status, body)) in _routes)
            {
                if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(status)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    });
                }
            }
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}
