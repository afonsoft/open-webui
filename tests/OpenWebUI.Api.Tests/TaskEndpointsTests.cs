using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes dos endpoints de tarefas auxiliares de IA (título, follow-ups, tags, queries) com provedor Ollama mockado.</summary>
[TestFixture, IsolateEnvironment]
public class TaskEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-tasks-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@tasks.local", "senha123");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());

        var baseUrl = StartMock();
        var connResponse = await _client.PostAsJsonAsync("/api/v1/configs/connections",
            new ConnectionsConfig([baseUrl], [], []));
        Assert.That(connResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _client.DefaultRequestHeaders.Authorization = null;
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
            var body = string.Empty;
            if (ctx.Request.HasEntityBody)
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                body = await reader.ReadToEndAsync(ct);
            }

            // Cada tarefa tem um prompt distinto: o corpo da completion revela qual resposta servir.
            var (status, json) = path switch
            {
                "/api/tags" => (200, "{\"models\":[{\"model\":\"fake:1\",\"name\":\"fake:1\"}]}"),
                "/api/chat" when body.Contains("broken:1") => (500, "{}"),
                "/api/chat" when body.Contains("3-5 word title") => (200,
                    "{\"message\":{\"content\":\"\\\"Chat sobre .NET\\nlinha extra ignorada\\\"\"}}"),
                "/api/chat" when body.Contains("follow-up questions") => (200,
                    "{\"message\":{\"content\":\"[\\\"Pergunta um?\\\",\\\"Pergunta dois?\\\"]\"}}"),
                "/api/chat" when body.Contains("broad tags") => (200,
                    "{\"message\":{\"content\":\"[\\\" Tech \\\",\\\"FINANCE\\\",\\\"tech\\\",\\\" Saúde \\\",\\\"\\\"]\"}}"),
                "/api/chat" when body.Contains("web search queries") => (200,
                    "{\"message\":{\"content\":\"Aqui estão: [\\\"query um\\\",\\\"query dois\\\"] fim\"}}"),
                "/api/chat" => (200, "{\"message\":{\"content\":\"resposta do mock\"}}"),
                _ => (404, "{}"),
            };
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
            ctx.Response.Close();
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

    private static TaskGenerationRequest NewTaskRequest(string model = "fake:1", string? chatId = null) =>
        new(model,
            [
                new ChatCompletionMessage("user", "Como configurar o Ollama local?"),
                new ChatCompletionMessage("assistant", "Instale o Ollama e rode ollama serve."),
            ],
            chatId);

    [Test, Order(1)]
    public async Task Tasks_SemToken_Retorna401()
    {
        _client.DefaultRequestHeaders.Authorization = null;

        var response = await _client.PostAsJsonAsync(
            "/api/v1/tasks/title/completions", NewTaskRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test, Order(2)]
    public async Task Title_ComMockOllama_RetornaTituloLimpo()
    {
        var user = await SignUpAsync("Title", "title@tasks.local", "senha123");
        UseToken(user.Token);

        var chat = await _client.PostAsJsonAsync("/api/v1/chats/",
            new ChatUpsertRequest("Chat de teste", ["fake:1"],
                [new ChatMessageModel("m1", "user", "Olá", null, 100)]));
        Assert.That(chat.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var chatId = (await chat.Content.ReadFromJsonAsync<ChatResponse>())!.Id;

        var response = await _client.PostAsJsonAsync(
            "/api/v1/tasks/title/completions", NewTaskRequest(chatId: chatId));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var result = await response.Content.ReadFromJsonAsync<TaskTitleResponse>();
        // Aspas são removidas e só a primeira linha do texto do mock vira título.
        Assert.That(result!.Title, Is.EqualTo("Chat sobre .NET"));
    }

    [Test, Order(3)]
    public async Task FollowUps_ComMockOllama_RetornaSugestoes()
    {
        var user = await SignUpAsync("Follow", "follow@tasks.local", "senha123");
        UseToken(user.Token);

        var response = await _client.PostAsJsonAsync(
            "/api/v1/tasks/follow_up/completions", NewTaskRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var result = await response.Content.ReadFromJsonAsync<TaskFollowUpsResponse>();
        Assert.That(result!.FollowUps, Is.EqualTo(new[] { "Pergunta um?", "Pergunta dois?" }));
    }

    [Test, Order(4)]
    public async Task Tags_ComMockOllama_NormalizaEDeduplica()
    {
        var user = await SignUpAsync("Tags", "tags@tasks.local", "senha123");
        UseToken(user.Token);

        var response = await _client.PostAsJsonAsync(
            "/api/v1/tasks/tags/completions", NewTaskRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var result = await response.Content.ReadFromJsonAsync<TaskTagsResponse>();
        // O endpoint apara, faz lowercase, remove vazias e deduplica as tags do mock.
        Assert.That(result!.Tags, Is.EqualTo(new[] { "tech", "finance", "saúde" }));
    }

    [Test, Order(5)]
    public async Task Queries_ComMockOllama_ExtraiArrayJson()
    {
        var user = await SignUpAsync("Queries", "queries@tasks.local", "senha123");
        UseToken(user.Token);

        var response = await _client.PostAsJsonAsync(
            "/api/v1/tasks/queries/completions", NewTaskRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var queries = json.GetProperty("queries")
            .EnumerateArray()
            .Select(q => q.GetString())
            .ToList();
        // O parser extrai o array JSON mesmo com texto ao redor na resposta do mock.
        Assert.That(queries, Is.EqualTo(new[] { "query um", "query dois" }));
    }

    [Test, Order(6)]
    public async Task Tasks_ProviderFalha_Title404EDemaisListasVazias()
    {
        var user = await SignUpAsync("Fail", "fail@tasks.local", "senha123");
        UseToken(user.Token);

        // "broken:1" faz o mock responder 500: título vira 404 e as listas vêm vazias.
        var title = await _client.PostAsJsonAsync(
            "/api/v1/tasks/title/completions", NewTaskRequest(model: "broken:1"));
        Assert.That(title.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var followUps = await _client.PostAsJsonAsync(
            "/api/v1/tasks/follow_up/completions", NewTaskRequest(model: "broken:1"));
        Assert.That(followUps.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var followUpsResult = await followUps.Content.ReadFromJsonAsync<TaskFollowUpsResponse>();
        Assert.That(followUpsResult!.FollowUps, Is.Empty);

        var tags = await _client.PostAsJsonAsync(
            "/api/v1/tasks/tags/completions", NewTaskRequest(model: "broken:1"));
        Assert.That(tags.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var tagsResult = await tags.Content.ReadFromJsonAsync<TaskTagsResponse>();
        Assert.That(tagsResult!.Tags, Is.Empty);

        var queries = await _client.PostAsJsonAsync(
            "/api/v1/tasks/queries/completions", NewTaskRequest(model: "broken:1"));
        Assert.That(queries.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var queriesJson = await queries.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(queriesJson.GetProperty("queries").GetArrayLength(), Is.EqualTo(0));
    }
}
