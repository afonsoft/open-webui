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
[TestFixture]
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
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-pass-{Guid.NewGuid():N}.db");
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
            File.Delete(_dbPath);
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

    [Test, Order(1)]
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

    [Test, Order(2)]
    public async Task Ollama_Chat_Post_RepassaBodyAoProvider()
    {
        UseToken(_adminToken);
        var response = await _client.PostAsJsonAsync("/ollama/api/chat",
            new { model = "fake:1", messages = new[] { new { role = "user", content = "oi" } } });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("ola do fake:1"));
    }

    [Test, Order(3)]
    public async Task OpenAi_Models_EncaminhaKeyMascarada()
    {
        UseToken(_adminToken);
        var response = await _client.GetAsync("/openai/models");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("gpt-mock"));
        Assert.That(_lastAuth, Is.EqualTo("Bearer test-key"));
    }

    [Test, Order(4)]
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

    [Test, Order(5)]
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

    [Test, Order(6)]
    public async Task SemAuth_401()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var response = await _client.GetAsync("/ollama/api/tags");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        var openai = await _client.GetAsync("/openai/models");
        Assert.That(openai.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
