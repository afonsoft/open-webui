using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>Cobertura de branches do ProviderService: múltiplas URLs, dedup, erros do provider, timeout, roteamento e payloads de borda.</summary>
[TestFixture]
public class ProviderServiceTests
{
    private string _dbPath = null!;
    private AppDbContext _db = null!;
    private ConfigService _config = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private string _mockUrl = null!;
    private TimeSpan? _clientTimeout;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly ConcurrentDictionary<string, (int Status, string Body, int DelayMs)> _routes = new();

    private sealed record RecordedRequest(string Path, string? Authorization, string Body);

    private sealed class FakeHttpClientFactory(TimeSpan? timeout) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient();
            if (timeout is not null)
            {
                client.Timeout = timeout.Value;
            }

            return client;
        }
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-provider-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        await DatabaseMigrator.MigrateAsync(_db);
        _config = new ConfigService(_db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        _mockUrl = StartMock();
    }

    [SetUp]
    public void SetUp()
    {
        _routes.Clear();
        _requests.Clear();
        _clientTimeout = null;
    }

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

    private ProviderService NewService() =>
        new(new FakeHttpClientFactory(_clientTimeout), _config,
            NullLogger<ProviderService>.Instance,
            new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

    private Task SetConnectionsAsync(
        IReadOnlyList<string> ollama, IReadOnlyList<string> openAi, IReadOnlyList<string> keys) =>
        _config.SetAsync("connections", new ConnectionsConfig(ollama, openAi, keys));

    private static ChatCompletionRequest Req(string model, string? connection = null) =>
        new(model, [new ChatCompletionMessage("user", "oi")], Stream: false, Connection: connection);

    private static async Task<List<string>> DrainAsync(IAsyncEnumerable<string> lines)
    {
        var result = new List<string>();
        await foreach (var line in lines)
        {
            result.Add(line);
        }

        return result;
    }

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
                ctx = await _mock.GetContextAsync().WaitAsync(ct);
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Cada requisição responde em task própria para que uma rota
            // lenta (timeout) não serialize as demais.
            _ = Task.Run(() => RespondAsync(ctx, ct), ct);
        }
    }

    private async Task RespondAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var path = ctx.Request.Url!.AbsolutePath;
            var body = ctx.Request.HasEntityBody
                ? await new StreamReader(ctx.Request.InputStream).ReadToEndAsync(ct)
                : string.Empty;
            _requests.Enqueue(new RecordedRequest(
                path, ctx.Request.Headers["Authorization"], body));

            var (status, payload, delayMs) =
                _routes.TryGetValue(path, out var route) ? route : (404, "{}", 0);
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, ct);
            }

            var bytes = Encoding.UTF8.GetBytes(payload);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
            ctx.Response.Close();
        }
        catch (Exception)
        {
            // Cliente desconectou (timeout) ou o mock encerrou: ignora.
        }
    }

    [Test, Order(1)]
    public async Task ListModels_OllamaMultiplasUrls_DedupIgnoraVaziasEForaDoAr()
    {
        _routes["/api/tags"] = (200,
            "{\"models\":[" +
            "{\"model\":\"m1\",\"name\":\"M1\"}," +
            "{\"name\":\"m2\"}," +              // id cai para o campo name
            "{\"model\":\"\",\"name\":\"x\"}," + // id vazio: ignorado
            "{\"size\":1}]}", 0);               // sem id: ignorado
        // Segunda URL é o mesmo mock com barra final: todos os modelos viram duplicatas.
        await SetConnectionsAsync(
            [_mockUrl, _mockUrl + "/", "   ", "http://localhost:1"], [], []);

        var models = await NewService().ListModelsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(models.Select(m => m.Id), Is.EqualTo(new[] { "m1", "m2" }));
            Assert.That(models[0].Name, Is.EqualTo("M1"));
            Assert.That(models[0].Provider, Is.EqualTo("ollama"));
            Assert.That(models[0].OwnedBy, Is.EqualTo("ollama"));
            Assert.That(models[1].Name, Is.EqualTo("m2"));
            // A URL em branco e a fora do ar não geraram erro.
            Assert.That(
                _requests.Count(r => r.Path == "/api/tags"), Is.EqualTo(2));
        });
    }

    [Test, Order(2)]
    public async Task ListModels_OpenAiMultiplasUrls_ChavesPorIndiceOwnedByEDedup()
    {
        _routes["/models"] = (200,
            "{\"data\":[" +
            "{\"id\":\"gpt-1\",\"owned_by\":\"org\"}," +
            "{\"id\":\"\"}," +                    // id vazio: ignorado
            "{\"id\":\"gpt-1\"}," +               // duplicata entre URLs: ignorada
            "{\"id\":\"gpt-2\"}]}", 0);           // sem owned_by → OwnedBy null
        // Só uma chave para duas URLs: a segunda sai sem Authorization.
        await SetConnectionsAsync([], [_mockUrl, _mockUrl, "   "], ["sk-a"]);

        var models = await NewService().ListModelsAsync();

        var calls = _requests.Where(r => r.Path == "/models").ToList();
        Assert.Multiple(() =>
        {
            Assert.That(models.Select(m => m.Id), Is.EqualTo(new[] { "gpt-1", "gpt-2" }));
            Assert.That(models[0].Provider, Is.EqualTo("openai"));
            Assert.That(models[0].OwnedBy, Is.EqualTo("org"));
            Assert.That(models[1].OwnedBy, Is.Null);
            Assert.That(calls, Has.Count.EqualTo(2));
            Assert.That(calls[0].Authorization, Is.EqualTo("Bearer sk-a"));
            Assert.That(calls[1].Authorization, Is.Null);
        });
    }

    [Test, Order(3)]
    public async Task ListModels_Providers500_RetornaVazio()
    {
        _routes["/api/tags"] = (500, "{}", 0);
        _routes["/models"] = (500, "{}", 0);
        await SetConnectionsAsync([_mockUrl], [_mockUrl], []);

        var models = await NewService().ListModelsAsync();

        Assert.That(models, Is.Empty);
    }

    [Test, Order(4)]
    public async Task ListModels_JsonInvalido_PropagaJsonException()
    {
        // Objeto sem a chave "models": cai no ?? [] e não gera modelo.
        _routes["/api/tags"] = (200, "{}", 0);
        // JSON malformado no provider OpenAI: JsonException não é capturada.
        _routes["/models"] = (200, "isso-nao-e-json", 0);
        await SetConnectionsAsync([_mockUrl], [_mockUrl], []);

        // JsonNode.Parse lança JsonReaderException (interna, deriva de JsonException).
        Assert.CatchAsync<JsonException>(async () => await NewService().ListModelsAsync());
    }

    [Test, Order(17)]
    public async Task ListModels_RaizNaoObjeto_LancaInvalidOperation()
    {
        // Raiz JsonArray/JsonValue não tem indexador por nome: o serviço lança
        // InvalidOperationException (comportamento atual, não filtrado pelo catch).
        _routes["/api/tags"] = (200, "[]", 0);
        await SetConnectionsAsync([_mockUrl], [], []);

        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await NewService().ListModelsAsync());
    }

    [Test, Order(5)]
    public async Task ListModels_OpenAiLento_TimeoutIgnorado()
    {
        _routes["/api/tags"] = (200, "{\"models\":[{\"model\":\"m1\",\"name\":\"m1\"}]}", 0);
        _routes["/models"] = (200, "{\"data\":[]}", 10_000); // nunca responde a tempo
        _clientTimeout = TimeSpan.FromMilliseconds(200);
        await SetConnectionsAsync([_mockUrl], [_mockUrl], []);

        var models = await NewService().ListModelsAsync();

        // TaskCanceledException do timeout é engolida: só o Ollama respondeu.
        Assert.That(models.Select(m => m.Id), Is.EqualTo(new[] { "m1" }));
    }

    [Test, Order(6)]
    public async Task Complete_ModeloOllama_RetornaConteudo_EStringVaziaSemContent()
    {
        _routes["/api/tags"] = (200, "{\"models\":[{\"model\":\"m1\",\"name\":\"m1\"}]}", 0);
        _routes["/api/chat"] = (200, "{\"message\":{\"content\":\"resposta ok\"},\"done\":true}", 0);
        await SetConnectionsAsync([_mockUrl], [], []);
        var svc = NewService();

        var text = await svc.CompleteAsync(Req("m1"));
        Assert.That(text, Is.EqualTo("resposta ok"));

        // Resposta sem message.content cai no ?? string.Empty.
        _routes["/api/chat"] = (200, "{\"done\":true}", 0);
        var empty = await svc.CompleteAsync(Req("m1"));
        Assert.That(empty, Is.EqualTo(string.Empty));
    }

    [Test, Order(7)]
    public async Task Complete_ModeloForaDoOllama_RoteiaParaOpenAiComApiKey()
    {
        _routes["/api/tags"] = (200, "{\"models\":[{\"model\":\"m1\",\"name\":\"m1\"}]}", 0);
        _routes["/chat/completions"] = (200,
            "{\"choices\":[{\"message\":{\"content\":\"oi openai\"}}]}", 0);
        await SetConnectionsAsync([_mockUrl], [_mockUrl], ["sk-b"]);

        var text = await NewService().CompleteAsync(Req("gpt-9"));

        Assert.Multiple(() =>
        {
            Assert.That(text, Is.EqualTo("oi openai"));
            // "gpt-9" não está no Ollama e há URL OpenAI → provider openai.
            Assert.That(
                _requests.Last(r => r.Path == "/chat/completions").Authorization,
                Is.EqualTo("Bearer sk-b"));
        });
    }

    [Test, Order(8)]
    public async Task Resolve_ConexaoExplicita_PulaResolucaoPorModelo()
    {
        _routes["/api/tags"] = (200, "{\"models\":[{\"model\":\"m1\",\"name\":\"m1\"}]}", 0);
        _routes["/chat/completions"] = (200,
            "{\"choices\":[{\"message\":{\"content\":\"oi openai\"}}]}", 0);
        await SetConnectionsAsync([_mockUrl], [_mockUrl], []);

        // m1 existe no Ollama, mas Connection="openai" manda direto pro OpenAI.
        var text = await NewService().CompleteAsync(Req("m1", connection: "openai"));

        Assert.Multiple(() =>
        {
            Assert.That(text, Is.EqualTo("oi openai"));
            // A resolução não consultou a listagem de modelos.
            Assert.That(_requests.Any(r => r.Path == "/api/tags"), Is.False);
        });
    }

    [Test, Order(9)]
    public async Task Resolve_ConexaoInvalida_LancaInvalidOperation()
    {
        await SetConnectionsAsync([_mockUrl], [_mockUrl], []);
        var svc = NewService();

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await svc.CompleteAsync(Req("m1", connection: "bogus")));
        Assert.That(ex!.Message, Does.Contain("m1"));

        // O mesmo switch lança ao enumerar o stream.
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await DrainAsync(svc.StreamCompletionAsync(Req("m1", connection: "bogus"))));

        // OpenAI só com URL em branco: FirstOpenAiConnection não acha nenhuma.
        await SetConnectionsAsync([], ["   "], []);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await svc.CompleteAsync(Req("g", connection: "openai")));
    }

    [Test, Order(10)]
    public async Task Resolve_SemNenhumProvider_LancaSemUrlOllama()
    {
        await SetConnectionsAsync([], [], []);
        var svc = NewService();

        // Modelo desconhecido sem OpenAI cai no ramo "ollama" sem URL → lança.
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await svc.CompleteAsync(Req("qualquer")));
        Assert.That(ex!.Message, Does.Contain("Ollama"));

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await DrainAsync(svc.StreamCompletionAsync(Req("qualquer"))));

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await svc.CompleteWithToolsAsync(Req("qualquer")));
    }

    [Test, Order(11)]
    public async Task Complete_Provider500_PropagaHttpRequestException()
    {
        _routes["/api/chat"] = (500, "{}", 0);
        _routes["/chat/completions"] = (500, "{}", 0);
        await SetConnectionsAsync([_mockUrl], [_mockUrl], []);
        var svc = NewService();

        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<HttpRequestException>(async () =>
                await svc.CompleteAsync(Req("m1", connection: "ollama")));
            Assert.ThrowsAsync<HttpRequestException>(async () =>
                await svc.CompleteAsync(Req("g", connection: "openai")));
            Assert.ThrowsAsync<HttpRequestException>(async () =>
                await svc.CompleteWithToolsAsync(Req("m1", connection: "ollama")));
            Assert.ThrowsAsync<HttpRequestException>(async () =>
                await DrainAsync(svc.StreamCompletionAsync(Req("g", connection: "openai"))));
        });
    }

    [Test, Order(12)]
    public async Task Stream_Ollama_PulaLinhasVaziasEJsonInvalido_TerminaEmDone()
    {
        _routes["/api/chat"] = (200,
            "{\"message\":{\"content\":\"a\"},\"done\":false}\n" +
            "\n" +
            "linha-que-nao-e-json\n" +
            "{\"done\":true}\n", 0); // sem message: content vazio + done
        await SetConnectionsAsync([_mockUrl], [], []);

        var lines = await DrainAsync(
            NewService().StreamCompletionAsync(Req("m1", connection: "ollama")));

        Assert.Multiple(() =>
        {
            Assert.That(lines, Has.Count.EqualTo(3));
            var chunk1 = JsonNode.Parse(lines[0]["data: ".Length..])!;
            Assert.That(
                chunk1["choices"]![0]!["delta"]!["content"]!.GetValue<string>(),
                Is.EqualTo("a"));
            Assert.That(chunk1["object"]!.GetValue<string>(),
                Is.EqualTo("chat.completion.chunk"));
            Assert.That(chunk1["choices"]![0]!["finish_reason"], Is.Null);
            var chunk2 = JsonNode.Parse(lines[1]["data: ".Length..])!;
            Assert.That(chunk2["choices"]![0]!["finish_reason"]!.GetValue<string>(),
                Is.EqualTo("stop"));
            Assert.That(lines[2], Is.EqualTo("data: [DONE]"));
        });
    }

    [Test, Order(13)]
    public async Task Stream_Ollama_ParamsViramOptions_MaxTokensPermaneceNoTopo()
    {
        _routes["/api/chat"] = (200, "{\"done\":true}\n", 0);
        await SetConnectionsAsync([_mockUrl], [], []);
        using var doc = JsonDocument.Parse("[1,2]");
        var request = new ChatCompletionRequest(
            "m1",
            [
                new ChatCompletionMessage("user", "oi", ToolCallId: "tc-9",
                    ToolCallsJson: "[{\"id\":\"c\",\"function\":{\"name\":\"f\",\"arguments\":{}}}]"),
                new ChatCompletionMessage("assistant", "certo"),
            ],
            Stream: true,
            Connection: "ollama",
            Tools: [doc.RootElement],
            Params: new Dictionary<string, object>
            {
                ["temperature"] = 0.7,      // double → options
                ["top_p"] = 0.9,            // double → options
                ["top_k"] = 5,              // int → options
                ["num_predict"] = 16,       // int → options
                ["repeat_penalty"] = 1.1f,  // float → options
                ["seed"] = 7L,              // long → options
                ["stop"] = "fim",           // string → options
                ["max_tokens"] = 99,        // não está na lista de movidos: fica no topo
                ["decimalParam"] = 1.5m,    // decimal
                ["boolParam"] = true,       // bool
                ["nullParam"] = null!,      // null
                ["jsonParam"] = doc.RootElement, // JsonElement
                ["objParam"] = new[] { 1, 2 },   // fallback SerializeToNode
            });

        var lines = await DrainAsync(NewService().StreamCompletionAsync(request));

        var sent = JsonNode.Parse(_requests.Last(r => r.Path == "/api/chat").Body)!;
        Assert.Multiple(() =>
        {
            Assert.That(lines, Has.Count.EqualTo(2)); // chunk done + [DONE]
            var options = sent["options"]!;
            Assert.That(options["temperature"]!.GetValue<double>(), Is.EqualTo(0.7));
            Assert.That(options["top_p"]!.GetValue<double>(), Is.EqualTo(0.9));
            Assert.That(options["top_k"]!.GetValue<int>(), Is.EqualTo(5));
            Assert.That(options["num_predict"]!.GetValue<int>(), Is.EqualTo(16));
            Assert.That(options["repeat_penalty"]!.GetValue<float>(), Is.EqualTo(1.1f));
            Assert.That(options["seed"]!.GetValue<long>(), Is.EqualTo(7L));
            Assert.That(options["stop"]!.GetValue<string>(), Is.EqualTo("fim"));
            // Chaves fora da lista ficam no topo (inclui max_tokens).
            Assert.That(sent["temperature"], Is.Null);
            Assert.That(sent["max_tokens"]!.GetValue<int>(), Is.EqualTo(99));
            Assert.That(sent["decimalParam"]!.GetValue<decimal>(), Is.EqualTo(1.5m));
            Assert.That(sent["boolParam"]!.GetValue<bool>(), Is.True);
            // Param null vira propriedade JSON null (JsonObject mantém a chave).
            Assert.That(sent.AsObject().ContainsKey("nullParam"), Is.True);
            Assert.That(sent["nullParam"], Is.Null);
            Assert.That(sent["jsonParam"]!.AsArray(), Has.Count.EqualTo(2));
            Assert.That(sent["objParam"]!.AsArray(), Has.Count.EqualTo(2));
            var msg0 = sent["messages"]![0]!;
            Assert.That(msg0["tool_call_id"]!.GetValue<string>(), Is.EqualTo("tc-9"));
            Assert.That(msg0["tool_calls"], Is.Not.Null);
            Assert.That(sent["messages"]![1]!["tool_call_id"], Is.Null);
            Assert.That(sent["tools"], Is.Not.Null);
            Assert.That(sent["stream"]!.GetValue<bool>(), Is.True);
        });
    }

    [Test, Order(14)]
    public async Task Stream_OpenAi_EncaminhaLinhasEParaEmDone()
    {
        _routes["/chat/completions"] = (200,
            "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n" +
            "\n" +
            "data: [DONE]\n" +
            "data: {\"depois\":true}\n", 0); // linha após DONE não deve sair
        await SetConnectionsAsync([], [_mockUrl], []); // sem chave: sai sem Authorization

        var lines = await DrainAsync(
            NewService().StreamCompletionAsync(Req("g", connection: "openai")));

        Assert.Multiple(() =>
        {
            Assert.That(lines, Is.EqualTo(new[]
            {
                "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}",
                "data: [DONE]",
            }));
            Assert.That(
                _requests.Last(r => r.Path == "/chat/completions").Authorization,
                Is.Null);
        });
    }

    [Test, Order(15)]
    public async Task CompleteWithTools_OpenAi_NormalizaToolCallsEArguments()
    {
        _routes["/chat/completions"] = (200,
            "{\"choices\":[{\"message\":{\"content\":\"\",\"tool_calls\":[" +
            "{\"id\":\"c1\",\"function\":{\"name\":\"f1\",\"arguments\":\"{\\\"a\\\":1}\"}}," +
            "{\"function\":{\"name\":\"f2\",\"arguments\":{\"b\":2}}}," +
            "{\"id\":\"c3\",\"function\":{\"name\":null}}]}}]}", 0); // sem name: ignorada
        await SetConnectionsAsync([], [_mockUrl], []);

        var result = await NewService().CompleteWithToolsAsync(Req("g", connection: "openai"));

        Assert.Multiple(() =>
        {
            Assert.That(result.ToolCalls, Has.Count.EqualTo(2));
            Assert.That(result.ToolCalls[0].Id, Is.EqualTo("c1"));
            Assert.That(result.ToolCalls[0].Name, Is.EqualTo("f1"));
            // arguments como string JSON (JsonValue): preservada.
            Assert.That(result.ToolCalls[0].ArgumentsJson, Is.EqualTo("{\"a\":1}"));
            // sem id: gera GUID "N"; arguments objeto: serializado.
            Assert.That(result.ToolCalls[1].Id, Does.Match("^[0-9a-f]{32}$"));
            Assert.That(result.ToolCalls[1].Name, Is.EqualTo("f2"));
            Assert.That(result.ToolCalls[1].ArgumentsJson, Is.EqualTo("{\"b\":2}"));
            Assert.That(result.ToolCallsJson, Does.Contain("f1"));
        });
    }

    [Test, Order(16)]
    public async Task CompleteWithTools_Ollama_SemCalls_RetornaCallsJsonVazio()
    {
        _routes["/api/chat"] = (200, "{\"message\":{\"content\":\"so texto\"}}", 0);
        await SetConnectionsAsync([_mockUrl], [], []);

        var result = await NewService().CompleteWithToolsAsync(Req("m1", connection: "ollama"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.EqualTo("so texto"));
            Assert.That(result.ToolCalls, Is.Empty);
            Assert.That(result.ToolCallsJson, Is.EqualTo("[]"));
        });
    }
}
