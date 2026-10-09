using Microsoft.Extensions.Caching.Memory;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Detecção automática de capacidades de providers OpenAI-compatíveis:
/// classifica /models por modalidade+heurística, probeia os endpoints
/// e grava audio/images/video/embedding somente em chaves ausentes.
/// </summary>
[TestFixture]
public class ProviderCapabilityTests
{
    private string _dbPath = null!;
    private AppDbContext _db = null!;
    private MemoryCache _cache = null!;
    private ConfigService _config = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private string _mockUrl = null!;
    private readonly ConcurrentBag<string> _paths = new();
    private readonly ConcurrentDictionary<string, (int Status, string Body, string ContentType)> _routes = new();

    private sealed class RealHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-cap-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        await DatabaseMigrator.MigrateAsync(_db);
        _mockUrl = StartMock();
    }

    [SetUp]
    public async Task SetUp()
    {
        _routes.Clear();
        while (_paths.TryTake(out _)) { }

        await _db.ConfigEntries.ExecuteDeleteAsync();
        _db.ChangeTracker.Clear(); // delete em massa não desanexa entidades
        // Cache por teste em campo (dispose no TearDown) — um using local
        // disporia o cache antes do teste usar o _config.
        _cache = new MemoryCache(new MemoryCacheOptions());
        _config = new ConfigService(_db, _cache);
    }

    [TearDown]
    public void TearDown() => _cache.Dispose();

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _mockCts.Cancel();
        _mock.Stop();
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private ProviderCapabilityService NewService() =>
        new(new RealHttpClientFactory(), _config,
            NullLogger<ProviderCapabilityService>.Instance);

    private string StartMock()
    {
        var random = new Random();
        var port = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            port = random.Next(41000, 61000);
            _mock = new HttpListener();
            _mock.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                _mock.Start();
                break;
            }
            catch (HttpListenerException)
            {
                _mock.Close();
            }
        }
        if (!_mock.IsListening)
        {
            throw new InvalidOperationException("Nenhuma porta livre para o mock.");
        }
        _mockCts = new CancellationTokenSource();
        _ = Task.Run(() => MockLoopAsync(_mockCts.Token));
        return $"http://localhost:{port}";
    }

    private async Task MockLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _mock.GetContextAsync().WaitAsync(ct);
            }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (OperationCanceledException) { return; }

            _ = Task.Run(() =>
            {
                try
                {
                    var path = ctx.Request.Url!.AbsolutePath;
                    _paths.Add(path);
                    var (status, payload, contentType) = _routes.TryGetValue(path, out var route)
                        ? route : (404, "{}", "application/json");
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    ctx.Response.StatusCode = status;
                    ctx.Response.ContentType = contentType;
                    ctx.Response.OutputStream.Write(bytes);
                    ctx.Response.Close();
                }
                catch (HttpListenerException) { /* cliente desconectou — ignora */ }
                catch (IOException) { /* cliente desconectou — ignora */ }
                catch (ObjectDisposedException) { /* listener parou */ }
            }, ct);
        }
    }

    private Task SetConnectionsAsync(params string[] urls) =>
        _config.SetAsync("connections", new ConnectionsConfig(
            [], urls, urls.Select(_ => "sk-test").ToList()));

    [Test]
    public async Task AutoConfigure_CatalogoCompleto_GravaAudioImagemEEmbedding()
    {
        _routes["/models"] = (200,
            """
            {"data":[
                {"id":"gpt-4o"},
                {"id":"vendor/tts-1","output_modalities":["audio"]},
                {"id":"vendor/whisper-1","input_modalities":["audio"]},
                {"id":"vendor/flux-1","output_modalities":["image"]},
                {"id":"vendor/sora-2","output_modalities":["video"]},
                {"id":"vendor/text-embedding-3-small"}
            ]}
            """, "application/json");
        _routes["/audio/speech"] = (200, "RIFF-fake-audio", "audio/mpeg");
        _routes["/images/generations"] = (200, """{"data":[{"b64_json":"x"}]}""", "application/json");
        _routes["/videos"] = (404, "{}", "application/json"); // rota inexistente
        _routes["/embeddings"] = (200, """{"data":[{"embedding":[0.1]}]}""", "application/json");
        await SetConnectionsAsync(_mockUrl);

        await NewService().AutoConfigureAsync();

        var audio = await _config.GetAsync<AudioConfig?>("audio.config", null);
        var images = await _config.GetAsync<ImagesConfig?>("images.config", null);
        var video = await _config.GetAsync<VideoConfig?>("video.config", null);
        var embed = await _config.GetAsync<string?>("rag.embedding.model", null);

        Assert.Multiple(() =>
        {
            Assert.That(audio, Is.Not.Null);
            Assert.That(audio!.TtsEngine, Is.EqualTo("provider"));
            Assert.That(audio.TtsProvider, Is.EqualTo(_mockUrl));
            Assert.That(audio.TtsModel, Is.EqualTo("vendor/tts-1"));
            Assert.That(audio.SttEngine, Is.EqualTo("provider"));
            Assert.That(audio.SttModel, Is.EqualTo("vendor/whisper-1"));
            Assert.That(images, Is.Not.Null);
            Assert.That(images!.Enabled, Is.True);
            Assert.That(images.Model, Is.EqualTo("vendor/flux-1"));
            Assert.That(images.ApiKey, Is.EqualTo("sk-test"));
            // /videos 404 → sem config de vídeo.
            Assert.That(video, Is.Null);
            Assert.That(embed, Is.EqualTo("vendor/text-embedding-3-small"));
        });
    }

    [Test]
    public async Task AutoConfigure_ConfigExistente_NuncaSobrescreve()
    {
        var custom = AudioConfig.Default with
        {
            TtsEngine = "openai", TtsModel = "meu-tts", TtsBaseUrl = "http://x",
        };
        await _config.SetAsync("audio.config", custom);
        _routes["/models"] = (200,
            """{"data":[{"id":"vendor/tts-1","output_modalities":["audio"]}]}""",
            "application/json");
        _routes["/audio/speech"] = (200, "a", "audio/mpeg");

        await SetConnectionsAsync(_mockUrl);

        await NewService().AutoConfigureAsync();

        var audio = await _config.GetAsync<AudioConfig?>("audio.config", null);
        Assert.That(audio!.TtsModel, Is.EqualTo("meu-tts"));
    }

    [Test]
    public async Task AutoConfigure_SemModalidades_UsaHeuristicaDeId()
    {
        // Catálogo sem output_modalities: ids ainda classificam por regex.
        _routes["/models"] = (200,
            """{"data":[{"id":"acme/gpt-image-1"},{"id":"acme/whisper-large"}]}""",
            "application/json");
        _routes["/images/generations"] = (200, """{"data":[{}]}""", "application/json");
        _routes["/audio/speech"] = (404, "{}", "application/json");
        await SetConnectionsAsync(_mockUrl);

        await NewService().AutoConfigureAsync();

        var images = await _config.GetAsync<ImagesConfig?>("images.config", null);
        var audio = await _config.GetAsync<AudioConfig?>("audio.config", null);
        Assert.Multiple(() =>
        {
            Assert.That(images, Is.Not.Null);
            Assert.That(images!.Model, Is.EqualTo("acme/gpt-image-1"));
            // STT por heurística mesmo sem TTS funcionar.
            Assert.That(audio, Is.Not.Null);
            Assert.That(audio!.SttModel, Is.EqualTo("acme/whisper-large"));
            Assert.That(audio.TtsEngine, Is.EqualTo("none"));
        });
    }

    [Test]
    public async Task AutoConfigure_ModelsFalha_NaoGravaNada()
    {
        _routes["/models"] = (500, "{}", "application/json");
        await SetConnectionsAsync(_mockUrl);

        await NewService().AutoConfigureAsync();

        var audio = await _config.GetAsync<AudioConfig?>("audio.config", null);
        var images = await _config.GetAsync<ImagesConfig?>("images.config", null);
        var video = await _config.GetAsync<VideoConfig?>("video.config", null);
        var embed = await _config.GetAsync<string?>("rag.embedding.model", null);

        Assert.Multiple(() =>
        {
            Assert.That(audio, Is.Null);
            Assert.That(images, Is.Null);
            Assert.That(video, Is.Null);
            Assert.That(embed, Is.Null);
        });
    }

    [Test]
    public async Task AutoConfigure_VideoRouteOk_GravaVideoConfig()
    {
        _routes["/models"] = (200,
            """{"data":[{"id":"vendor/sora-2","output_modalities":["video"]}]}""",
            "application/json");
        _routes["/videos"] = (200, """{"id":"vid_1","status":"queued"}""", "application/json");
        await SetConnectionsAsync(_mockUrl);

        await NewService().AutoConfigureAsync();

        var video = await _config.GetAsync<VideoConfig?>("video.config", null);
        Assert.Multiple(() =>
        {
            Assert.That(video, Is.Not.Null);
            Assert.That(video!.Enabled, Is.True);
            Assert.That(video.Model, Is.EqualTo("vendor/sora-2"));
            Assert.That(video.BaseUrl, Is.EqualTo(_mockUrl));
        });
    }
}
