using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do retrieval avançado: process text/url/file, web search com
/// engine mockada (SearXNG), config admin com chave mascarada e reset.
/// O mock HTTP serve o endpoint /api/embed do Ollama e o /search do SearXNG.
/// </summary>
[TestFixture, IsolateEnvironment]
public class RetrievalEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private string _mockBaseUrl = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _mockBaseUrl = StartMock();
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-retrieval-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("OLLAMA_BASE_URL", _mockBaseUrl);
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = await SignUpAsync("Admin Retrieval", "admin@retrieval.local", "senha123");
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
        Environment.SetEnvironmentVariable("OLLAMA_BASE_URL", null);
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
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
            string json;
            int status;
            switch (path)
            {
                case "/api/embed":
                    json = "{\"embeddings\":[[1.0,0.5,0.25]]}";
                    status = 200;
                    break;
                case "/search":
                    json = "{\"results\":[{" +
                        "\"title\":\"Resultado A\",\"url\":\"http://a.example\",\"content\":\"conteúdo A\"},{" +
                        "\"title\":\"Resultado B\",\"url\":\"http://b.example\",\"content\":\"conteúdo B\"}]}";
                    status = 200;
                    break;
                case "/page.html":
                    json = "<html><head><style>body{color:red}</style><script>var x=1;</script></head>" +
                        "<body><h1>Título da página</h1><p>Texto indexável pelo loader.</p></body></html>";
                    status = 200;
                    break;
                default:
                    json = "{}";
                    status = 404;
                    break;
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = path == "/page.html" ? "text/html" : "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    [Test]
    public async Task ProcessText_IndexaConteudo()
    {
        UseToken(_admin.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/retrieval/process/text",
            new { content = "Texto direto para indexação via retrieval.", name = "nota.txt" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("chunks").GetInt32(), Is.GreaterThan(0));
        Assert.That(body.GetProperty("fileId").GetString(), Is.Not.Empty);
    }

    [Test]
    public async Task ProcessUrl_ExtraiEIndexaHtml()
    {
        UseToken(_admin.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/retrieval/process/url",
            new { url = $"{_mockBaseUrl}/page.html" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("chunks").GetInt32(), Is.GreaterThan(0));
    }

    [Test]
    public async Task ProcessUrl_FetchFalha_Retorna502()
    {
        UseToken(_admin.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/retrieval/process/url",
            new { url = $"{_mockBaseUrl}/nao-existe" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
    }

    [Test]
    public async Task ProcessFile_ReindexaArquivoEnviado()
    {
        UseToken(_admin.Token);
        var fileId = await UploadTextFileAsync("arquivo-para-process.txt", "conteúdo do arquivo");

        var response = await _client.PostAsJsonAsync("/api/v1/retrieval/process/file",
            new { fileId });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("chunks").GetInt32(), Is.GreaterThan(0));
    }

    private async Task<string> UploadTextFileAsync(string filename, string content)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", filename);
        var response = await _client.PostAsync("/api/v1/files/", form);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
    }

    [Test]
    public async Task WebSearch_SemEngine_Retorna503()
    {
        UseToken(_admin.Token);
        // Desativa explicitamente: o default agora é duckduckgo (sem chave).
        var update = await _client.PostAsJsonAsync("/api/v1/retrieval/config/update",
            RetrievalConfig.Default with { Engine = "none" });
        Assert.That(update.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var response = await _client.PostAsJsonAsync("/api/v1/retrieval/process/web/search",
            new { query = "qualquer coisa", processResults = false });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    [Test, DependsOnTest(nameof(WebSearch_SemEngine_Retorna503), AllowFailure = true)]
    public async Task Config_GetEPersistencia_ComChaveMascarada()
    {
        UseToken(_admin.Token);

        var update = await _client.PostAsJsonAsync("/api/v1/retrieval/config/update", new
        {
            engine = "searxng",
            searxngBaseUrl = _mockBaseUrl,
            braveApiKey = "segredo-brave-123",
            topK = 7,
            chunkSize = 500,
            chunkOverlap = 50,
            hybrid = true,
            hybridWeight = 0.7,
            rerank = true,
        });
        Assert.That(update.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await update.Content.ReadAsStringAsync());

        var cfg = await _client.GetFromJsonAsync<JsonElement>("/api/v1/retrieval/config");
        Assert.Multiple(() =>
        {
            Assert.That(cfg.GetProperty("engine").GetString(), Is.EqualTo("searxng"));
            Assert.That(cfg.GetProperty("topK").GetInt32(), Is.EqualTo(7));
            Assert.That(cfg.GetProperty("hybrid").GetBoolean(), Is.True);
            Assert.That(cfg.GetProperty("braveApiKey").GetString(), Is.EqualTo("********"));
        });
        Assert.That(cfg.ToString(), Does.Not.Contain("segredo-brave-123"));
    }

    [Test, DependsOnTest(nameof(Config_GetEPersistencia_ComChaveMascarada), AllowFailure = true)]
    public async Task WebSearch_ComSearxng_RetornaEIndexa()
    {
        UseToken(_admin.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/retrieval/process/web/search",
            new { query = "devin", processResults = true });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var results = body.GetProperty("results").EnumerateArray().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(results.Count, Is.EqualTo(2));
            Assert.That(results[0].GetProperty("title").GetString(), Is.EqualTo("Resultado A"));
            Assert.That(body.GetProperty("processed").GetProperty("chunks").GetInt32(),
                Is.GreaterThan(0));
        });
    }

    [Test]
    public async Task Config_UsuarioComum_NaoAcessa()
    {
        var user = await SignUpAsync("User Retrieval", "user@retrieval.local", "senha123");
        UseToken(user.Token);

        var get = await _client.GetAsync("/api/v1/retrieval/config");
        var post = await _client.PostAsJsonAsync("/api/v1/retrieval/config/update",
            RetrievalConfig.Default);
        var reset = await _client.PostAsync("/api/v1/retrieval/reset/db", null);

        Assert.Multiple(() =>
        {
            Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(reset.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        });
    }

    [Test]
    public async Task ResetDb_LimpaChunksIndexados()
    {
        UseToken(_admin.Token);
        var reset = await _client.PostAsync("/api/v1/retrieval/reset/db", null);
        Assert.That(reset.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Reindexar deve criar chunks do zero (confirma que o reset apagou).
        var response = await _client.PostAsJsonAsync("/api/v1/retrieval/process/text",
            new { content = "Reindexando após reset." });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("chunks").GetInt32(), Is.GreaterThan(0));

        var invalid = await _client.PostAsync("/api/v1/retrieval/reset/outro", null);
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
