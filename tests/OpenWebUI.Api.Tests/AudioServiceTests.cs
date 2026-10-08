using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do <see cref="AudioService"/> (SPEC-20261008-tests-coverage-gate
/// RF-002): resolução de provider cadastrado, matriz de engines TTS/STT
/// (openai/transformers/elevenlabs/azure/deepgram/whisper), headers de auth
/// próprios de cada engine, erros 501/502 e parsing das respostas.
/// </summary>
public class AudioServiceTests
{
    private AppDbContext _db = null!;
    private MemoryCache _cache = null!;
    private ConfigService _config = null!;
    private RoutingHandler _handler = null!;
    private AudioService _svc = null!;

    [SetUp]
    public void SetUp()
    {
        var path = Path.Join(Path.GetTempPath(), $"owui-audio-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path}").Options);
        DatabaseMigrator.MigrateAsync(_db).GetAwaiter().GetResult();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _config = new ConfigService(_db, _cache);
        _handler = new RoutingHandler();
        _svc = new AudioService(new StubFactory(_handler), _config);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _cache.Dispose();
        _handler.Dispose();
    }

    private static AudioConfig Cfg(
        string stt = "none", string tts = "none",
        string? sttUrl = null, string? sttKey = null,
        string? ttsUrl = null, string? ttsKey = null,
        string? sttModel = null, string? ttsModel = null,
        string? ttsVoice = null, string? azureRegion = null,
        string? sttProvider = null, string? ttsProvider = null) =>
        new(stt, sttUrl, sttKey, sttModel,
            tts, ttsUrl, ttsKey, ttsModel, ttsVoice, azureRegion,
            sttProvider, ttsProvider);

    private Task SetAudio(AudioConfig cfg) =>
        _config.SetAsync("audio.config", cfg, default);

    // ---------------- resolução de provider ----------------

    [Test]
    public async Task Resolve_SemProvider_PassaDireto()
    {
        var cfg = Cfg(tts: "openai", ttsUrl: "http://tts");
        await SetAudio(cfg);
        var r = await _svc.GetResolvedConfigAsync();
        Assert.That(r.TtsEngine, Is.EqualTo("openai"));
    }

    [Test]
    public async Task Resolve_ProviderEncontrado_ViraOpenAiComChave()
    {
        await _config.SetAsync("connections", new ConnectionsConfig(
            OllamaBaseUrls: [],
            OpenAiBaseUrls: ["http://api.test/v1/"],
            OpenAiApiKeys: ["sk-abc"]), default);
        await SetAudio(Cfg(
            stt: "provider", sttProvider: "http://api.test/v1",
            tts: "provider", ttsProvider: "http://api.test/v1"));
        var r = await _svc.GetResolvedConfigAsync();
        Assert.Multiple(() =>
        {
            Assert.That(r.SttEngine, Is.EqualTo("openai"));
            Assert.That(r.SttBaseUrl, Is.EqualTo("http://api.test/v1/"));
            Assert.That(r.SttApiKey, Is.EqualTo("sk-abc"));
            Assert.That(r.TtsEngine, Is.EqualTo("openai"));
            Assert.That(r.TtsApiKey, Is.EqualTo("sk-abc"));
        });
    }

    [Test]
    public async Task Resolve_ProviderNaoEncontrado_CaiParaNone()
    {
        await _config.SetAsync("connections", new ConnectionsConfig(
            OllamaBaseUrls: [], OpenAiBaseUrls: [], OpenAiApiKeys: []), default);
        await SetAudio(Cfg(stt: "provider", sttProvider: "http://sumiu.test"));
        var r = await _svc.GetResolvedConfigAsync();
        Assert.That(r.SttEngine, Is.EqualTo("none"));
    }

    [Test]
    public void Config_MasquaraChaves()
    {
        var cfg = Cfg(sttKey: "k1", ttsKey: "k2");
        var m = cfg.Masked();
        Assert.Multiple(() =>
        {
            Assert.That(m.SttApiKey, Is.EqualTo("********"));
            Assert.That(m.TtsApiKey, Is.EqualTo("********"));
            Assert.That(Cfg().Masked().SttApiKey, Is.Null);
        });
    }

    [Test]
    public void Config_EnabledFlags()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AudioConfig.Default.SttEnabled, Is.False);
            Assert.That(AudioConfig.Default.TtsEnabled, Is.False);
            Assert.That(Cfg(stt: "openai", sttUrl: "http://x").SttEnabled, Is.True);
            Assert.That(Cfg(stt: "deepgram", sttUrl: "http://x").SttEnabled, Is.True);
            Assert.That(Cfg(stt: "whisper", sttUrl: "http://x").SttEnabled, Is.True);
            Assert.That(Cfg(stt: "openai").SttEnabled, Is.False);
            Assert.That(Cfg(stt: "provider", sttProvider: "http://c").SttEnabled, Is.True);
            Assert.That(Cfg(tts: "openai", ttsUrl: "http://x").TtsEnabled, Is.True);
            Assert.That(Cfg(tts: "transformers", ttsUrl: "http://x").TtsEnabled, Is.True);
            Assert.That(Cfg(tts: "provider", ttsProvider: "http://c").TtsEnabled, Is.True);
            Assert.That(Cfg(tts: "elevenlabs", ttsKey: "k").TtsEnabled, Is.True);
            Assert.That(Cfg(tts: "elevenlabs").TtsEnabled, Is.False);
            Assert.That(Cfg(tts: "azure", ttsKey: "k", azureRegion: "brazilsouth").TtsEnabled, Is.True);
            Assert.That(Cfg(tts: "azure", ttsKey: "k", ttsUrl: "http://x").TtsEnabled, Is.True);
            Assert.That(Cfg(tts: "azure", ttsKey: "k").TtsEnabled, Is.False);
            Assert.That(Cfg(tts: "xyz").TtsEnabled, Is.False);
        });
    }

    // ---------------- SpeechAsync (TTS) ----------------

    [Test]
    public async Task Speech_TtsDesabilitado_Lanca501()
    {
        await Assert.ThrowsAsync<AudioDisabledException>(
            () => _svc.SpeechAsync("oi", null, null));
    }

    [Test]
    public async Task Speech_OpenAi_PostBearerEDefaults()
    {
        await SetAudio(Cfg(tts: "openai", ttsUrl: "http://tts.test/", ttsKey: "sk-x"));
        _handler.Respond(HttpStatusCode.OK,
            new ByteArrayContent(new byte[] { 1, 2, 3 }) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav") } });
        var r = await _svc.SpeechAsync("olá", null, null);
        Assert.Multiple(() =>
        {
            Assert.That(r.Audio, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(r.ContentType, Is.EqualTo("audio/wav"));
            Assert.That(_handler.LastRequest!.RequestUri!.ToString(),
                Is.EqualTo("http://tts.test/audio/speech"));
            Assert.That(_handler.LastRequest.Headers.Authorization!.Scheme, Is.EqualTo("Bearer"));
            Assert.That(_handler.LastRequest.Headers.Authorization.Parameter, Is.EqualTo("sk-x"));
        });
        var body = _handler.LastBody!;
        Assert.That(body, Does.Contain("tts-1").And.Contain("alloy"));
    }

    [Test]
    public async Task Speech_ElevenLabs_XiApiKeyEDefaultBase()
    {
        await SetAudio(Cfg(tts: "elevenlabs", ttsKey: "xi-k", ttsVoice: "voz-9"));
        _handler.Respond(HttpStatusCode.OK, new ByteArrayContent([9]));
        await _svc.SpeechAsync("txt", null, "model-x");
        Assert.Multiple(() =>
        {
            Assert.That(_handler.LastRequest!.RequestUri!.ToString(),
                Is.EqualTo("https://api.elevenlabs.io/v1/text-to-speech/voz-9"));
            Assert.That(_handler.LastRequest.Headers.GetValues("xi-api-key"), Does.Contain("xi-k"));
        });
    }

    [Test]
    public async Task Speech_ElevenLabsSemVoz_Lanca501()
    {
        await Assert.ThrowsAsync<AudioDisabledException>(async () =>
        {
            await SetAudio(Cfg(tts: "elevenlabs", ttsKey: "k"));
            await _svc.SpeechAsync("txt", null, null);
        });
    }

    [Test]
    public async Task Speech_Azure_SsmlEHeadersProprios()
    {
        await SetAudio(Cfg(tts: "azure", ttsKey: "az-k", azureRegion: "brazilsouth"));
        _handler.Respond(HttpStatusCode.OK, new ByteArrayContent([1]));
        await _svc.SpeechAsync("oi <mundo>", null, null);
        Assert.Multiple(() =>
        {
            Assert.That(_handler.LastRequest!.RequestUri!.ToString(), Is.EqualTo(
                "https://brazilsouth.tts.speech.microsoft.com/cognitiveservices/v1"));
            Assert.That(_handler.LastRequest.Headers
                .GetValues("Ocp-Apim-Subscription-Key"), Does.Contain("az-k"));
            Assert.That(_handler.LastRequest.Headers
                .GetValues("X-Microsoft-OutputFormat"), Does.Contain("audio-16khz-128kbitrate-mono-mp3"));
        });
        var ssml = _handler.LastBody!;
        Assert.That(ssml, Does.Contain("en-US-AriaNeural").And.Contain("oi &lt;mundo&gt;"));
    }

    [Test]
    public async Task Speech_ErroProvider_Lanca502()
    {
        await SetAudio(Cfg(tts: "openai", ttsUrl: "http://tts.test"));
        _handler.Respond(HttpStatusCode.InternalServerError, new StringContent("boom"));
        var ex = await Assert.ThrowsAsync<AudioProviderException>(
            () => _svc.SpeechAsync("oi", null, null));
        Assert.That(ex!.Message, Does.Contain("500"));
    }

    // ---------------- TranscribeAsync (STT) ----------------

    [Test]
    public async Task Transcribe_SttDesabilitada_Lanca501()
    {
        await Assert.ThrowsAsync<AudioDisabledException>(
            () => _svc.TranscribeAsync(new MemoryStream([1]), "a.wav"));
    }

    [Test]
    public async Task Transcribe_OpenAi_MultipartEText()
    {
        await SetAudio(Cfg(stt: "openai", sttUrl: "http://stt.test/", sttKey: "sk-s"));
        _handler.Respond(HttpStatusCode.OK,
            new StringContent("{\"text\":\"transcrição\"}", Encoding.UTF8, "application/json"));
        var text = await _svc.TranscribeAsync(new MemoryStream([1, 2]), "a.wav");
        Assert.Multiple(() =>
        {
            Assert.That(text, Is.EqualTo("transcrição"));
            Assert.That(_handler.LastRequest!.RequestUri!.ToString(),
                Is.EqualTo("http://stt.test/audio/transcriptions"));
            Assert.That(_handler.LastRequest.Headers.Authorization!.Scheme, Is.EqualTo("Bearer"));
        });
    }

    [Test]
    public async Task Transcribe_Deepgram_TokenUrlENested()
    {
        await SetAudio(Cfg(stt: "deepgram", sttUrl: "http://dg.test", sttKey: "dg-k"));
        _handler.Respond(HttpStatusCode.OK, new StringContent(
            "{\"results\":{\"channels\":[{\"alternatives\":[{\"transcript\":\"fala\"}]}]}}",
            Encoding.UTF8, "application/json"));
        var text = await _svc.TranscribeAsync(new MemoryStream([1]), "a.wav");
        Assert.Multiple(() =>
        {
            Assert.That(text, Is.EqualTo("fala"));
            Assert.That(_handler.LastRequest!.RequestUri!.ToString(),
                Is.EqualTo("http://dg.test/v1/listen?model=nova-2"));
            Assert.That(_handler.LastRequest.Headers.Authorization!.Scheme, Is.EqualTo("Token"));
            Assert.That(_handler.LastRequest.Content!.Headers.ContentType!.MediaType,
                Is.EqualTo("application/octet-stream"));
        });
    }

    [Test]
    public async Task Transcribe_SemText_RetornaVazio()
    {
        await SetAudio(Cfg(stt: "openai", sttUrl: "http://stt.test"));
        _handler.Respond(HttpStatusCode.OK,
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.That(await _svc.TranscribeAsync(new MemoryStream([1]), "a.wav"), Is.Empty);
    }

    [Test]
    public async Task Transcribe_ErroProvider_Lanca502()
    {
        await Assert.ThrowsAsync<AudioProviderException>(async () =>
        {
            await SetAudio(Cfg(stt: "openai", sttUrl: "http://stt.test"));
            _handler.Respond(HttpStatusCode.BadGateway, new StringContent("x"));
            await _svc.TranscribeAsync(new MemoryStream([1]), "a.wav");
        });
    }

    // ---------------- GetVoicesAsync / GetModelsAsync ----------------

    [Test]
    public async Task Voices_UrlsPorEngine()
    {
        var cases = new (string Engine, string? Url, string Esperado)[]
        {
            ("openai", "http://t.test/", "http://t.test/audio/voices"),
            ("elevenlabs", null, "https://api.elevenlabs.io/v1/voices"),
            ("azure", null, "https://westus.tts.speech.microsoft.com/cognitiveservices/voices/list"),
        };
        foreach (var (engine, url, esperado) in cases)
        {
            var cfg = engine switch
            {
                "elevenlabs" => Cfg(tts: engine, ttsKey: "k"),
                "azure" => Cfg(tts: engine, ttsKey: "k", azureRegion: "westus"),
                _ => Cfg(tts: engine, ttsUrl: url, ttsKey: "k"),
            };
            await SetAudio(cfg);
            _handler.Respond(HttpStatusCode.OK,
                new StringContent("{\"voices\":[]}", Encoding.UTF8, "application/json"));
            await _svc.GetVoicesAsync();
            Assert.That(_handler.LastRequest!.RequestUri!.ToString(),
                Is.EqualTo(esperado), engine);
        }
    }

    [Test]
    public async Task Voices_TtsDesabilitado_Lanca501()
    {
        await Assert.ThrowsAsync<AudioDisabledException>(() => _svc.GetVoicesAsync());
    }

    [Test]
    public async Task Models_UrlsPorEstado()
    {
        // STT habilitada → /models da STT.
        await SetAudio(Cfg(stt: "openai", sttUrl: "http://stt.test/", sttKey: "k"));
        _handler.Respond(HttpStatusCode.OK,
            new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json"));
        await _svc.GetModelsAsync();
        Assert.That(_handler.LastRequest!.RequestUri!.ToString(),
            Is.EqualTo("http://stt.test/models"));

        // Só TTS openai → /models da TTS.
        await SetAudio(Cfg(tts: "openai", ttsUrl: "http://tts.test"));
        await _svc.GetModelsAsync();
        Assert.That(_handler.LastRequest!.RequestUri!.ToString(),
            Is.EqualTo("http://tts.test/models"));

        // TTS elevenlabs → /v1/models.
        await SetAudio(Cfg(tts: "elevenlabs", ttsKey: "k"));
        await _svc.GetModelsAsync();
        Assert.That(_handler.LastRequest!.RequestUri!.ToString(),
            Is.EqualTo("https://api.elevenlabs.io/v1/models"));

        // TTS transformers → /models.
        await SetAudio(Cfg(tts: "transformers", ttsUrl: "http://tr.test"));
        await _svc.GetModelsAsync();
        Assert.That(_handler.LastRequest!.RequestUri!.ToString(),
            Is.EqualTo("http://tr.test/models"));
    }

    [Test]
    public async Task Models_TudoDesabilitado_Lanca501()
    {
        await Assert.ThrowsAsync<AudioDisabledException>(() => _svc.GetModelsAsync());
    }

    // ---------------- helpers ----------------

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private byte[] _body = "{}"u8.ToArray();
        private string? _mediaType = "application/json";

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public void Respond(HttpStatusCode status, HttpContent content)
        {
            using (content)
            {
                _status = status;
                _body = content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                _mediaType = content.Headers.ContentType?.MediaType;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            HttpContent fresh = _mediaType is null || _mediaType == "application/octet-stream"
                ? new ByteArrayContent(_body)
                : new StringContent(Encoding.UTF8.GetString(_body), Encoding.UTF8, _mediaType);
            if (fresh is ByteArrayContent && _mediaType is not null)
                fresh.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_mediaType);
            return new HttpResponseMessage(_status) { Content = fresh };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}
