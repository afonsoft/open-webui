using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes do endpoint de analytics (agregações admin-only).</summary>
[TestFixture]
public class AnalyticsEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-analytics-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@analytics.local", "senha123");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
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
    public async Task Analytics_UsuarioComum_Retorna403()
    {
        var user = await SignUpAsync("User", "user@analytics.local", "senha123");
        UseToken(user.Token);

        var response = await _client.GetAsync("/api/v1/analytics");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test, Order(2)]
    public async Task Analytics_Admin_RetornaAgregacoes()
    {
        UseToken(_adminToken);
        var user = await SignUpAsync("Ativo", "ativo@analytics.local", "senha123");
        UseToken(user.Token);

        // Semeia um chat com mensagens.
        var created = await _client.PostAsJsonAsync("/api/v1/chats/",
            new ChatUpsertRequest("chat", ["m1"],
            [
                new ChatMessageModel("a", "user", "oi", null, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                new ChatMessageModel("b", "assistant", "olá", "m1", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            ]));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        UseToken(_adminToken);
        var response = await _client.GetAsync("/api/v1/analytics?days=30");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var analytics = (await response.Content.ReadFromJsonAsync<AnalyticsResponse>())!;
        Assert.Multiple(() =>
        {
            Assert.That(analytics.Users.Total, Is.GreaterThanOrEqualTo(2));
            Assert.That(analytics.Series.Messages, Has.Count.EqualTo(30));
            Assert.That(analytics.Series.Chats, Has.Count.EqualTo(30));
            Assert.That(analytics.Series.Messages.Sum(d => d.Count), Is.GreaterThanOrEqualTo(2));
            Assert.That(analytics.Models.Any(m => m.Model == "m1"), Is.True);
        });
    }

    [Test, Order(3)]
    public async Task Analytics_DiasZerados_VoltamComZero()
    {
        UseToken(_adminToken);
        var analytics = (await _client.GetFromJsonAsync<AnalyticsResponse>("/api/v1/analytics?days=7"))!;

        Assert.That(analytics.Series.Messages, Has.Count.EqualTo(7));
        // Série contínua: dias sem dados vêm com 0, último dia tem dados semeados.
        Assert.That(analytics.Series.Messages.Select(d => d.Day).Distinct().Count(), Is.EqualTo(7));
    }
}
