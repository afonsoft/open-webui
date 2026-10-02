using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do audio server-side: config admin mascarada, TTS /speech,
/// STT /transcriptions (multipart), /voices /models e /capabilities —
/// com provider OpenAI-compatible mockado via HttpListener.
/// </summary>
[TestFixture]
public class AudioEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private string _mockBaseUrl = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;

    private static readonly byte[] FakeMp3 = [0x49, 0x44, 0x33, 0x01, 0x02, 0x03];

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _mockBaseUrl = StartMock();
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-audio-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = await SignUpAsync("Admin Audio", "admin@audio.local", "senha123");
        UseToken(_admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _mockCts.Cancel();
        _mock.Stop();
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email, string password)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, password));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private string StartMock()
    {
        var random = new Random();
        var port = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            port = random.Next(40000, 60000);
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
                ctx = await _mock.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return;
            }

            var path = ctx.Request.Url!.AbsolutePath;
            switch (path)
            {
                case "/audio/speech":
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "audio/mpeg";
                    await ctx.Response.OutputStream.WriteAsync(FakeMp3);
                    break;
                case "/audio/transcriptions":
                    var json = Encoding.UTF8.GetBytes("{\"text\":\"transcrição mockada\"}");
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(json);
                    break;
                case "/audio/voices":
                    var voices = Encoding.UTF8.GetBytes(
                        "{\"voices\":[{\"voice_id\":\"alloy\"},{\"voice_id\":\"nova\"}]}");
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(voices);
                    break;
                case "/models":
                    var models = Encoding.UTF8.GetBytes(
                        "{\"data\":[{\"id\":\"tts-1\"},{\"id\":\"whisper-1\"}]}");
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(models);
                    break;
                default:
                    ctx.Response.StatusCode = 404;
                    break;
            }
            ctx.Response.Close();
        }
    }

    [Test, Order(1)]
    public async Task Speech_SemProvider_Retorna501()
    {
        UseToken(_admin.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/audio/speech",
            new { input = "olá mundo" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotImplemented));
    }

    [Test, Order(2)]
    public async Task Transcriptions_SemProvider_Retorna501()
    {
        UseToken(_admin.Token);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([1, 2, 3]), "file", "voz.wav");

        var response = await _client.PostAsync("/api/v1/audio/transcriptions", form);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotImplemented));
    }

    [Test, Order(3)]
    public async Task Config_PersisteEMascaraChaves()
    {
        UseToken(_admin.Token);

        var update = await _client.PostAsJsonAsync("/api/v1/audio/config", new
        {
            sttEngine = "openai",
            sttBaseUrl = _mockBaseUrl,
            sttApiKey = "segredo-stt",
            sttModel = "whisper-1",
            ttsEngine = "openai",
            ttsBaseUrl = _mockBaseUrl,
            ttsApiKey = "segredo-tts",
            ttsModel = "tts-1",
            ttsVoice = "alloy",
        });
        Assert.That(update.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await update.Content.ReadAsStringAsync());

        var cfg = await _client.GetFromJsonAsync<JsonElement>("/api/v1/audio/config");
        Assert.Multiple(() =>
        {
            Assert.That(cfg.GetProperty("sttApiKey").GetString(), Is.EqualTo("********"));
            Assert.That(cfg.GetProperty("ttsApiKey").GetString(), Is.EqualTo("********"));
            Assert.That(cfg.ToString(), Does.Not.Contain("segredo-"));
        });
    }

    [Test, Order(4)]
    public async Task Speech_ComProvider_RetornaAudio()
    {
        UseToken(_admin.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/audio/speech",
            new { input = "olá mundo", voice = "alloy" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("audio/mpeg"));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(FakeMp3));
    }

    [Test, Order(5)]
    public async Task Transcriptions_ComProvider_RetornaTexto()
    {
        UseToken(_admin.Token);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([1, 2, 3, 4]), "file", "voz.wav");

        var response = await _client.PostAsync("/api/v1/audio/transcriptions", form);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("text").GetString(), Is.EqualTo("transcrição mockada"));
    }

    [Test, Order(6)]
    public async Task Transcriptions_FormatoInvalido_Retorna400()
    {
        UseToken(_admin.Token);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([1, 2, 3]), "file", "doc.txt");

        var response = await _client.PostAsync("/api/v1/audio/transcriptions", form);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(7)]
    public async Task VoicesEModels_PassthroughDoProvider()
    {
        UseToken(_admin.Token);

        var voices = await _client.GetFromJsonAsync<JsonElement>("/api/v1/audio/voices");
        Assert.That(voices.GetProperty("voices").GetArrayLength(), Is.EqualTo(2));

        var models = await _client.GetFromJsonAsync<JsonElement>("/api/v1/audio/models");
        Assert.That(models.GetProperty("data").GetArrayLength(), Is.EqualTo(2));
    }

    [Test, Order(8)]
    public async Task Capabilities_RefleteConfig()
    {
        UseToken(_admin.Token);

        var caps = await _client.GetFromJsonAsync<JsonElement>("/api/v1/audio/capabilities");
        Assert.Multiple(() =>
        {
            Assert.That(caps.GetProperty("stt").GetBoolean(), Is.True);
            Assert.That(caps.GetProperty("tts").GetBoolean(), Is.True);
        });
    }

    [Test, Order(9)]
    public async Task Config_UsuarioComum_NaoAcessa()
    {
        var user = await SignUpAsync("User Audio", "user@audio.local", "senha123");
        UseToken(user.Token);

        var get = await _client.GetAsync("/api/v1/audio/config");
        var post = await _client.PostAsJsonAsync("/api/v1/audio/config", AudioConfig.Default);

        Assert.Multiple(() =>
        {
            Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        });
    }
}
