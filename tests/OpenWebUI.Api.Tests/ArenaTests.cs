using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes da slice arena-leaderboard: batalha A/B, voto, ELO e grants de modelo.</summary>
[TestFixture]
public class ArenaTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;
    private AuthResponse _user = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var mockBase = StartMock();
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-arena-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("OLLAMA_BASE_URL", mockBase);
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        Environment.SetEnvironmentVariable("OLLAMA_BASE_URL", null);

        _admin = await SignUpAsync("Admin", "admin-arena@test.local", "senha123");
        UseToken(_admin.Token);
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
        _client.DefaultRequestHeaders.Authorization = null;
        _user = await SignUpAsync("User", "user-arena@test.local", "senha123");
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
            if (path == "/api/chat")
            {
                using var reader = new StreamReader(ctx.Request.InputStream);
                var body = await reader.ReadToEndAsync(ct);
                var model = JsonDocument.Parse(body).RootElement
                    .GetProperty("model").GetString();
                json = $"{{\"message\":{{\"content\":\"resposta de {model}\"}}}}";
                ctx.Response.StatusCode = 200;
            }
            else if (path == "/api/tags")
            {
                json = "{\"models\":[{\"name\":\"m1\"},{\"name\":\"m2\"},{\"name\":\"m3\"}]}";
                ctx.Response.StatusCode = 200;
            }
            else
            {
                json = "{}";
                ctx.Response.StatusCode = 404;
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
            ctx.Response.Close();
        }
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email, string password)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new { name, email, password });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

    private static string ArenaMeta(params string[] ids) =>
        $"{{\"arena\": true, \"model_ids\": [{string.Join(",", ids.Select(i => $"\"{i}\""))}]}}";

    private string _arenaModelId = string.Empty;
    private string _battleId = string.Empty;

    [Test, Order(1)]
    public async Task ArenaModel_Criado_SemBaseModel() // RF-001
    {
        UseToken(_user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/models/create",
            new ModelEntryUpsertRequest("Arena Teste", null, null, null, null,
                ArenaMeta("m1", "m2", "m3")));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var model = (await created.Content.ReadFromJsonAsync<ModelEntryResponse>())!;
        _arenaModelId = model.Id;
        Assert.That(model.BaseModelId, Is.Null);

        // arena com 1 concorrente é rejeitada
        var bad = await _client.PostAsJsonAsync("/api/v1/models/create",
            new ModelEntryUpsertRequest("Arena Ruim", null, null, null, null,
                ArenaMeta("m1")));
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(2)]
    public async Task ArenaCompletion_DuasRespostasAnonimas() // RF-001
    {
        UseToken(_user.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/completions")
        {
            Content = JsonContent.Create(new ChatCompletionRequest(
                $"arena:{_arenaModelId}",
                [new ChatCompletionMessage("user", "oi")],
                Stream: true)),
        };
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var sse = await response.Content.ReadAsStringAsync();

        Assert.That(sse, Does.Contain("battle_id"));
        var payload = JsonDocument.Parse(
            sse.Split('\n').First(l => l.Contains("battle_id"))["data:".Length..].Trim());
        var arena = payload.RootElement.GetProperty("arena");
        _battleId = arena.GetProperty("battle_id").GetString()!;
        var responses = arena.GetProperty("responses").EnumerateArray().ToList();
        Assert.That(responses, Has.Count.EqualTo(2));
        Assert.That(responses.Select(r => r.GetProperty("label").GetString()),
            Is.EquivalentTo(new[] { "A", "B" }));
        // conteúdo não revela o modelo (mock ecoa "resposta de <model>")
        Assert.That(responses[0].GetProperty("content").GetString(),
            Does.StartWith("resposta de "));
    }

    [Test, Order(3)]
    public async Task ArenaVote_RevelaModelosEAtualizaElo() // RF-002/RF-003
    {
        UseToken(_user.Token);
        var vote = await _client.PostAsJsonAsync("/api/v1/evaluations/arena/feedback",
            new ArenaFeedbackRequest(_battleId, "a"));
        vote.EnsureSuccessStatusCode();
        var result = (await vote.Content.ReadFromJsonAsync<ArenaFeedbackResponse>())!;

        var valid = new[] { "m1", "m2", "m3" };
        Assert.That(valid, Does.Contain(result.ModelA));
        Assert.That(valid, Does.Contain(result.ModelB));
        Assert.That(result.ModelA, Is.Not.EqualTo(result.ModelB));
        Assert.That(result.RatingA, Is.GreaterThan(1000));
        Assert.That(result.RatingB, Is.LessThan(1000));

        // voto duplo é rejeitado
        var again = await _client.PostAsJsonAsync("/api/v1/evaluations/arena/feedback",
            new ArenaFeedbackRequest(_battleId, "b"));
        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(4)]
    public async Task Leaderboard_OrdenaPorRating() // RF-003
    {
        UseToken(_admin.Token);
        var board = await _client.GetFromJsonAsync<List<LeaderboardEntryResponse>>(
            "/api/v1/evaluations/leaderboard");
        Assert.That(board, Is.Not.Null);
        Assert.That(board!, Has.Count.EqualTo(2));
        Assert.That(board![0].Rating, Is.GreaterThan(board[1].Rating));
        Assert.That(board.Sum(e => e.Battles), Is.EqualTo(2));
    }

    [Test, Order(5)]
    public async Task AccessGrants_RestringemListagem() // RF-004
    {
        // modelos "public" seedados direto no SQLite do app
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        InsertPublicModel("pub-restrito",
            $"[{{\"principal_type\":\"user\",\"principal_id\":\"{_admin.User.Id}\",\"permission\":\"read\"}}]",
            now);
        InsertPublicModel("pub-aberto", null, now);

        UseToken(_user.Token);
        var list = await _client.GetFromJsonAsync<ModelListResponse>("/api/models");
        Assert.That(list!.Data.Any(m => m.Id == "pub-restrito"), Is.False);
        Assert.That(list!.Data.Any(m => m.Id == "pub-aberto"), Is.True);

        UseToken(_admin.Token);
        var adminList = await _client.GetFromJsonAsync<ModelListResponse>("/api/models");
        Assert.That(adminList!.Data.Any(m => m.Id == "pub-restrito"), Is.True);
        Assert.That(adminList!.Data.Any(m => m.Id == "pub-aberto"), Is.True);
    }

    private void InsertPublicModel(string id, string? grants, long now)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ModelEntries
                (Id, UserId, Name, BaseModelId, SystemPrompt, ParamsJson, ProfileImageUrl,
                 SuggestionPromptsJson, IsActive, CreatedAt, UpdatedAt, MetaJson, AccessGrantsJson)
            VALUES ($id, 'public', $name, 'm1', NULL, NULL, NULL, NULL, 1, $now, $now, NULL, $grants)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", $"Modelo {id}");
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$grants", (object?)grants ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
