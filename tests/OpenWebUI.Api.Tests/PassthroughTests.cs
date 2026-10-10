using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes da slice passthrough-routers: proxy autenticado de /ollama/* e
/// /openai/* para os providers configurados, com streaming preservado,
/// key mascarada server-side e 503/404 conforme a regra.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class PassthroughTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;
    private string _mockBase = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;

    /// <summary>Último Authorization header recebido pelo mock OpenAI.</summary>
    private string? _lastAuth;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-pass-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@pass.local", "senha123");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);

        _mockBase = StartMock();
        await _client.PostAsJsonAsync("/api/v1/configs/connections",
            new ConnectionsConfig([_mockBase], [_mockBase + "/v1"], ["test-key"]));
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
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private string StartMock()
    {
        var port = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            port = new Random().Next(40000, 60000);
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
            try
            {
                switch (path)
                {
                    case "/api/tags":
                        WriteJson(ctx, 200, "{\"models\":[{\"model\":\"fake:1\",\"name\":\"fake:1\"}]}");
                        break;
                    case "/api/version":
                        WriteJson(ctx, 200, "{\"version\":\"0.0.0-mock\"}");
                        break;
                    case "/api/chat":
                        {
                            using var reader = new StreamReader(ctx.Request.InputStream);
                            var body = await reader.ReadToEndAsync();
                            var model = JsonDocument.Parse(body).RootElement.GetProperty("model").GetString();
                            WriteJson(ctx, 200, $"{{\"message\":{{\"content\":\"ola do {model}\"}}}}");
                            break;
                        }
                    case "/v1/models":
                        _lastAuth = ctx.Request.Headers["Authorization"];
                        WriteJson(ctx, 200, "{\"data\":[{\"id\":\"gpt-mock\",\"owned_by\":\"mock\"}]}");
                        break;
                    case "/api/pull":
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/x-ndjson";
                        var pull = Encoding.UTF8.GetBytes(
                            "{\"status\":\"pulling manifest\"}\n" +
                            "{\"status\":\"downloading\",\"completed\":50,\"total\":100}\n" +
                            "{\"status\":\"success\"}\n");
                        await ctx.Response.OutputStream.WriteAsync(pull);
                        ctx.Response.Close();
                        break;
                    case "/api/create":
                    case "/api/delete":
                    case "/api/copy":
                        WriteJson(ctx, 200, "{}");
                        break;
                    case "/v1/audio/speech":
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "audio/mpeg";
                        await ctx.Response.OutputStream.WriteAsync(
                            Encoding.UTF8.GetBytes("FAKEAUDIOBYTES"));
                        ctx.Response.Close();
                        break;
                    case "/v1/audio/transcriptions":
                        WriteJson(ctx, 200, "{\"text\":\"ola transcrito\"}");
                        break;
                    case "/v1/images/generations":
                        WriteJson(ctx, 200,
                            "{\"data\":[{\"b64_json\":\"aW1hZ2Vt\"}]}");
                        break;
                    case "/v1/chat/completions":
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "text/event-stream";
                        var sse = Encoding.UTF8.GetBytes(
                            "data: {\"choices\":[{\"delta\":{\"content\":\"Oi\"}}]}\n\n" +
                            "data: [DONE]\n\n");
                        await ctx.Response.OutputStream.WriteAsync(sse);
                        ctx.Response.Close();
                        break;
                    default:
                        if (path.StartsWith("/api/blobs/", StringComparison.Ordinal))
                        {
                            await HandleBlobAsync(ctx);
                            break;
                        }
                        WriteJson(ctx, 404, "{}");
                        break;
                }
            }
            catch (HttpListenerException)
            {
                return;
            }
        }
    }

    /// <summary>Armazenamento fake de blobs por digest (para roundtrip).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _blobs = new();

    private async Task HandleBlobAsync(HttpListenerContext ctx)
    {
        var digest = ctx.Request.Url!.AbsolutePath["/api/blobs/".Length..];
        switch (ctx.Request.HttpMethod)
        {
            case "POST":
                using (var ms = new MemoryStream())
                {
                    await ctx.Request.InputStream.CopyToAsync(ms);
                    _blobs[digest] = ms.ToArray();
                }
                WriteJson(ctx, 200, "{}");
                break;
            case "GET":
                if (_blobs.TryGetValue(digest, out var data))
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/octet-stream";
                    await ctx.Response.OutputStream.WriteAsync(data);
                    ctx.Response.Close();
                }
                else
                {
                    WriteJson(ctx, 404, "{}");
                }
                break;
            case "HEAD":
                ctx.Response.StatusCode = _blobs.ContainsKey(digest) ? 200 : 404;
                ctx.Response.Close();
                break;
            default:
                WriteJson(ctx, 405, "{}");
                break;
        }
    }

    private static void WriteJson(HttpListenerContext ctx, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email, string password)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, password));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    [Test]
    public async Task Ollama_Tags_DevolveRespostaDoProvider()
    {
        UseToken(_adminToken);
        var response = await _client.GetAsync("/ollama/api/tags");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("fake:1"));

        var version = await _client.GetAsync("/ollama/api/version");
        Assert.That(await version.Content.ReadAsStringAsync(), Does.Contain("0.0.0-mock"));
    }

    [Test]
    public async Task Ollama_Chat_Post_RepassaBodyAoProvider()
    {
        UseToken(_adminToken);
        var response = await _client.PostAsJsonAsync("/ollama/api/chat",
            new { model = "fake:1", messages = new[] { new { role = "user", content = "oi" } } });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("ola do fake:1"));
    }

    [Test]
    public async Task OpenAi_Models_EncaminhaKeyMascarada()
    {
        UseToken(_adminToken);
        var response = await _client.GetAsync("/openai/models");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("gpt-mock"));
        Assert.That(_lastAuth, Is.EqualTo("Bearer test-key"));
    }

    [Test]
    public async Task OpenAi_ChatCompletions_PreservaSse()
    {
        UseToken(_adminToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/openai/chat/completions")
        {
            Content = JsonContent.Create(new { model = "gpt-mock", stream = true }),
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType,
            Is.EqualTo("text/event-stream"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("data:").And.Contain("[DONE]"));
    }

    [Test]
    public async Task SemProvider_503_EIndexForaDeFaixa_503()
    {
        UseToken(_adminToken);
        await _client.PostAsJsonAsync("/api/v1/configs/connections",
            new ConnectionsConfig([], [], []));
        try
        {
            var response = await _client.GetAsync("/ollama/api/tags");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        }
        finally
        {
            await _client.PostAsJsonAsync("/api/v1/configs/connections",
                new ConnectionsConfig([_mockBase], [_mockBase + "/v1"], ["test-key"]));
        }

        var outOfRange = await _client.GetAsync("/ollama/5/api/tags");
        Assert.That(outOfRange.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    [Test]
    public async Task SemAuth_401()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var response = await _client.GetAsync("/ollama/api/tags");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        var openai = await _client.GetAsync("/openai/models");
        Assert.That(openai.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Ollama_Pull_RepassaStreamNdjson()
    {
        UseToken(_adminToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ollama/api/pull")
        {
            Content = JsonContent.Create(new { name = "fake:1" }),
        };
        using var response = await _client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("pulling manifest")
            .And.Contain("downloading").And.Contain("success"));
    }

    [Test]
    public async Task Ollama_CreateDeleteCopy_Proxied()
    {
        UseToken(_adminToken);
        var create = await _client.PostAsJsonAsync("/ollama/api/create",
            new { name = "novo", from = "fake:1" });
        var copy = await _client.PostAsJsonAsync("/ollama/api/copy",
            new { source = "fake:1", destination = "fake:2" });
        using var deleteReq = new HttpRequestMessage(HttpMethod.Delete, "/ollama/api/delete")
        {
            Content = JsonContent.Create(new { name = "fake:2" }),
        };
        var delete = await _client.SendAsync(deleteReq);

        Assert.Multiple(() =>
        {
            Assert.That(create.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(copy.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(delete.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        });
    }

    [Test]
    public async Task Ollama_Blobs_RoundtripEDigestInvalido()
    {
        UseToken(_adminToken);
        var digest = $"sha256:{new string('a', 64)}";
        var payload = Encoding.UTF8.GetBytes("blob-de-teste");

        using var content = new ByteArrayContent(payload);
        var upload = await _client.PostAsync($"/ollama/api/blobs/{digest}", content);
        Assert.That(upload.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var headReq = new HttpRequestMessage(HttpMethod.Head,
            $"/ollama/api/blobs/{digest}");
        var head = await _client.SendAsync(headReq);
        Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var download = await _client.GetByteArrayAsync($"/ollama/api/blobs/{digest}");
        Assert.That(download, Is.EqualTo(payload));

        var invalid = await _client.GetAsync("/ollama/api/blobs/nao-e-digest");
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task OpenAi_AudioEImages_Passthrough()
    {
        UseToken(_adminToken);
        var speech = await _client.PostAsJsonAsync("/openai/audio/speech",
            new { model = "tts-1", input = "ola", voice = "alloy" });
        Assert.That(speech.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await speech.Content.ReadAsByteArrayAsync(),
            Is.EqualTo(Encoding.UTF8.GetBytes("FAKEAUDIOBYTES")));

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("audio")), "file", "a.wav");
        var stt = await _client.PostAsync("/openai/audio/transcriptions", form);
        Assert.That(await stt.Content.ReadAsStringAsync(), Does.Contain("ola transcrito"));

        var images = await _client.PostAsJsonAsync("/openai/images/generations",
            new { prompt = "gato" });
        Assert.That(await images.Content.ReadAsStringAsync(), Does.Contain("b64_json"));
    }

    [Test]
    public async Task ProviderMorto_Devolve502()
    {
        UseToken(_adminToken);
        await _client.PostAsJsonAsync("/api/v1/configs/connections",
            new ConnectionsConfig(["http://localhost:1"], [], []));
        try
        {
            var response = await _client.PostAsJsonAsync("/ollama/api/pull",
                new { name = "x" });
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
        }
        finally
        {
            await _client.PostAsJsonAsync("/api/v1/configs/connections",
                new ConnectionsConfig([_mockBase], [_mockBase + "/v1"], ["test-key"]));
        }
    }
}
