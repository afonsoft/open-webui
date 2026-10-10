using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes dos novos engines de busca (slice retrieval-v2): google_pse, jina,
/// exa, kagi e perplexity, todos apontados para um HttpListener mockado via
/// os campos de base URL da config, mais validação de engine/rerank_engine.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class RetrievalEnginesTests
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
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-engines-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("OLLAMA_BASE_URL", _mockBaseUrl);
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = await SignUpAsync("Admin Engines", "admin@engines.local", "senha123");
        UseToken(_admin.Token);
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
            catch (ObjectDisposedException)
            {
                return;
            }

            var path = ctx.Request.Url!.AbsolutePath;
            string json;
            int status;
            switch (path)
            {
                // google_pse
                case "/customsearch/v1":
                    json = "{\"items\":[{" +
                        "\"title\":\"G1\",\"link\":\"http://g1.example\",\"snippet\":\"trecho g1\"}]}";
                    status = 200;
                    break;
                // jina
                case "/jina":
                    json = "{\"data\":[{" +
                        "\"title\":\"J1\",\"url\":\"http://j1.example\",\"description\":\"trecho j1\"}]}";
                    status = 200;
                    break;
                // exa
                case "/exa/search":
                    json = "{\"results\":[{" +
                        "\"title\":\"E1\",\"url\":\"http://e1.example\",\"text\":\"trecho e1\"}]}";
                    status = 200;
                    break;
                // kagi (só t==0 são resultados web)
                case "/kagi/api/v0/search":
                    json = "{\"data\":[{" +
                        "\"t\":0,\"url\":\"http://k1.example\",\"title\":\"K1\",\"snippet\":\"trecho k1\"},{" +
                        "\"t\":1,\"url\":\"http://kx.example\",\"title\":\"KX\",\"snippet\":\"ignorado\"}]}";
                    status = 200;
                    break;
                // perplexity: resposta de chat completions com citations
                case "/pplx/chat/completions":
                    json = "{\"citations\":[\"http://p1.example\",\"http://p2.example\"]}";
                    status = 200;
                    break;
                case "/api/embed":
                    json = "{\"embeddings\":[[1.0,0.5,0.25]]}";
                    status = 200;
                    break;
                default:
                    json = "{}";
                    status = 404;
                    break;
            }

            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            var buf = Encoding.UTF8.GetBytes(json);
            try
            {
                await ctx.Response.OutputStream.WriteAsync(buf, ct);
                ctx.Response.Close();
            }
            catch (HttpListenerException)
            {
                return;
            }
        }
    }

    private RetrievalConfig _cfg = RetrievalConfig.Default;

    private async Task SetEngineAsync(RetrievalConfig config)
    {
        _cfg = config;
        var update = await _client.PostAsJsonAsync("/api/v1/retrieval/config/update", _cfg);
        Assert.That(update.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await update.Content.ReadAsStringAsync());
    }

    private async Task<List<JsonElement>> SearchAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/retrieval/process/web/search",
            new { query = "devin", processResults = false });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("results").EnumerateArray().ToList();
    }

    [Test]
    public async Task GooglePse_RetornaResultados()
    {
        UseToken(_admin.Token);
        await SetEngineAsync(_cfg with
        {
            Engine = "google_pse",
            GooglePseApiKey = "chave-g",
            GooglePseEngineId = "cx-1",
            GooglePseBaseUrl = _mockBaseUrl,
        });

        var results = await SearchAsync();
        Assert.Multiple(() =>
        {
            Assert.That(results.Count, Is.EqualTo(1));
            Assert.That(results[0].GetProperty("title").GetString(), Is.EqualTo("G1"));
            Assert.That(results[0].GetProperty("url").GetString(), Is.EqualTo("http://g1.example"));
        });
    }

    [Test]
    public async Task Jina_RetornaResultados()
    {
        await SetEngineAsync(_cfg with
        {
            Engine = "jina",
            JinaApiKey = "chave-j",
            JinaBaseUrl = $"{_mockBaseUrl}/jina",
        });

        var results = await SearchAsync();
        Assert.Multiple(() =>
        {
            Assert.That(results.Count, Is.EqualTo(1));
            Assert.That(results[0].GetProperty("title").GetString(), Is.EqualTo("J1"));
        });
    }

    [Test]
    public async Task Exa_RetornaResultados()
    {
        await SetEngineAsync(_cfg with
        {
            Engine = "exa",
            ExaApiKey = "chave-e",
            ExaBaseUrl = $"{_mockBaseUrl}/exa",
        });

        var results = await SearchAsync();
        Assert.Multiple(() =>
        {
            Assert.That(results.Count, Is.EqualTo(1));
            Assert.That(results[0].GetProperty("snippet").GetString(), Is.EqualTo("trecho e1"));
        });
    }

    [Test]
    public async Task Kagi_FiltraSomenteT0()
    {
        await SetEngineAsync(_cfg with
        {
            Engine = "kagi",
            KagiApiKey = "chave-k",
            KagiBaseUrl = $"{_mockBaseUrl}/kagi",
        });

        var results = await SearchAsync();
        Assert.Multiple(() =>
        {
            Assert.That(results.Count, Is.EqualTo(1));
            Assert.That(results[0].GetProperty("title").GetString(), Is.EqualTo("K1"));
        });
    }

    [Test]
    public async Task Perplexity_RetornaCitationsComoResultados()
    {
        await SetEngineAsync(_cfg with
        {
            Engine = "perplexity",
            PerplexityApiKey = "chave-p",
            PerplexityBaseUrl = $"{_mockBaseUrl}/pplx",
        });

        var results = await SearchAsync();
        Assert.Multiple(() =>
        {
            Assert.That(results.Count, Is.EqualTo(2));
            Assert.That(results[0].GetProperty("url").GetString(), Is.EqualTo("http://p1.example"));
        });
    }

    [Test]
    public async Task EngineSemChave_RetornaVazio()
    {
        // google_pse sem chave configurada → lista vazia (guard da implementação)
        await SetEngineAsync(_cfg with { Engine = "google_pse", GooglePseApiKey = "", GooglePseEngineId = "" });

        var results = await SearchAsync();
        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task Config_RejeitaEngineInvalidoERerankInvalido()
    {
        var badEngine = await _client.PostAsJsonAsync("/api/v1/retrieval/config/update",
            _cfg with { Engine = "nao-existe" });
        var badRerank = await _client.PostAsJsonAsync("/api/v1/retrieval/config/update",
            _cfg with { RerankEngine = "magico" });

        Assert.Multiple(() =>
        {
            Assert.That(badEngine.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(badRerank.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        });
    }

    [Test]
    public async Task Config_MascaraNovasChaves()
    {
        UseToken(_admin.Token);
        await SetEngineAsync(_cfg with
        {
            Engine = "jina",
            JinaApiKey = "segredo-jina",
            ExaApiKey = "segredo-exa",
            KagiApiKey = "segredo-kagi",
            PerplexityApiKey = "segredo-pplx",
            GooglePseApiKey = "segredo-gpse",
            RerankEngine = "external",
            RerankExternalApiKey = "segredo-rerank",
        });

        var cfg = await _client.GetFromJsonAsync<JsonElement>("/api/v1/retrieval/config");
        var raw = cfg.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(cfg.GetProperty("jinaApiKey").GetString(), Is.EqualTo("********"));
            Assert.That(cfg.GetProperty("exaApiKey").GetString(), Is.EqualTo("********"));
            Assert.That(cfg.GetProperty("kagiApiKey").GetString(), Is.EqualTo("********"));
            Assert.That(cfg.GetProperty("perplexityApiKey").GetString(), Is.EqualTo("********"));
            Assert.That(cfg.GetProperty("googlePseApiKey").GetString(), Is.EqualTo("********"));
            Assert.That(cfg.GetProperty("rerankExternalApiKey").GetString(), Is.EqualTo("********"));
            Assert.That(raw, Does.Not.Contain("segredo-"));
        });
    }
}
