using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes das páginas adicionadas na slice missing-pages: POST /api/config
/// (feature flags admin) e GET /api/v1/evaluations/feedbacks/list com usuário.
/// </summary>
[TestFixture]
public class MissingPagesTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-pages-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@pages.local", "senha123");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
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
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    [Test, Order(1)]
    public async Task PostApiConfig_SomenteAdmin()
    {
        var user = await SignUpAsync("User", "user@pages.local", "senha123");
        UseToken(user.Token);
        var forbidden = await _client.PostAsJsonAsync("/api/config", AdminConfig.Default);
        Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test, Order(2)]
    public async Task PostApiConfig_PersisteFlagERefleteNoGet()
    {
        UseToken(_adminToken);
        var before = await _client.GetFromJsonAsync<AppConfigResponse>("/api/config");
        Assert.That(before!.Features.EnableMemories, Is.True); // default agora ligado

        var updated = AdminConfig.Default with
        {
            DefaultUserRole = "user",
            EnableMemories = false,
        };
        var response = await _client.PostAsJsonAsync("/api/config", updated);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var after = await _client.GetFromJsonAsync<AppConfigResponse>("/api/config");
        Assert.That(after!.Features.EnableMemories, Is.False);
    }

    [Test, Order(3)]
    public async Task FeedbacksList_SomenteAdmin_ComNomeDoUsuario()
    {
        var user = await SignUpAsync("Avaliador", "avaliador@pages.local", "senha123");
        UseToken(user.Token);
        var saved = await _client.PostAsJsonAsync("/api/v1/evaluations/feedback",
            new FeedbackUpsertRequest("chat-1", "msg-1", "modelo-x", 1, "boa resposta"));
        Assert.That(saved.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var forbidden = await _client.GetAsync("/api/v1/evaluations/feedbacks/list");
        Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        UseToken(_adminToken);
        var list = await _client.GetFromJsonAsync<List<AdminFeedbackResponse>>(
            "/api/v1/evaluations/feedbacks/list");
        Assert.That(list, Is.Not.Null.And.Count.GreaterThanOrEqualTo(1));
        var fb = list!.Single(f => f.MessageId == "msg-1");
        Assert.Multiple(() =>
        {
            Assert.That(fb.UserName, Is.EqualTo("Avaliador"));
            Assert.That(fb.Rating, Is.EqualTo(1));
            Assert.That(fb.ModelId, Is.EqualTo("modelo-x"));
        });
    }
}
