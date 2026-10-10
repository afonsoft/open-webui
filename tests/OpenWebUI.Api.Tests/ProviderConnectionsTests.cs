using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Conexões tipadas (anthropic/google) em /api/v1/configs/connections:
/// validação de tipo e chave, mascaramento no GET e preservação de chaves.</summary>
[TestFixture, IsolateEnvironment]
public class ProviderConnectionsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-provconn-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest("Admin", "admin@provconn.local", "senha123"));
        _admin = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        UseToken(_admin.Token);
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static ConnectionsConfig BaseConnections(params ProviderConnection[] providers) =>
        new([], [], [], Providers: providers);

    [Test]
    public async Task Connections_TypedProviders_RoundTripMascaraChave()
    {
        var post = await _client.PostAsJsonAsync("/api/v1/configs/connections",
            BaseConnections(
                new ProviderConnection("anthropic", "https://api.anthropic.com", "sk-ant-1", "Claude"),
                new ProviderConnection("google", "https://generativelanguage.googleapis.com/v1beta", "gk-1", "Gemini")));
        Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var get = await _client.GetAsync("/api/v1/configs/connections");
        var raw = await get.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(raw, Does.Not.Contain("sk-ant-1"));
            Assert.That(raw, Does.Not.Contain("gk-1"));
        });

        var body = JsonDocument.Parse(raw).RootElement.GetProperty("providers");
        Assert.That(body.GetArrayLength(), Is.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(body[0].GetProperty("type").GetString(), Is.EqualTo("anthropic"));
            Assert.That(body[0].GetProperty("keyConfigured").GetBoolean(), Is.True);
            Assert.That(body[0].GetProperty("name").GetString(), Is.EqualTo("Claude"));
            Assert.That(body[1].GetProperty("type").GetString(), Is.EqualTo("google"));
            Assert.That(body[1].GetProperty("keyConfigured").GetBoolean(), Is.True);
        });
    }

    [Test]
    public async Task Connections_TipoDesconhecido_400()
    {
        var post = await _client.PostAsJsonAsync("/api/v1/configs/connections",
            BaseConnections(new ProviderConnection("bedrock", "https://example.com", "k")));
        Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Connections_AnthropicSemChave_400()
    {
        var post = await _client.PostAsJsonAsync("/api/v1/configs/connections",
            BaseConnections(new ProviderConnection("anthropic", "https://api.anthropic.com", null)));
        Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Connections_ChaveEmBranco_PreservaAnterior()
    {
        var post1 = await _client.PostAsJsonAsync("/api/v1/configs/connections",
            BaseConnections(new ProviderConnection("google", "https://generativelanguage.googleapis.com/v1beta", "gk-old")));
        Assert.That(post1.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Reenvia a linha sem a chave — servidor preserva a anterior.
        var post2 = await _client.PostAsJsonAsync("/api/v1/configs/connections",
            BaseConnections(new ProviderConnection("google", "https://generativelanguage.googleapis.com/v1beta", null)));
        Assert.That(post2.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = JsonDocument.Parse(await post2.Content.ReadAsStringAsync())
            .RootElement.GetProperty("providers");
        Assert.That(body[0].GetProperty("keyConfigured").GetBoolean(), Is.True);
    }

    [Test]
    public async Task Connections_PostSomenteAdmin()
    {
        var signup = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest("User", "user@provconn.local", "senha123"));
        var user = (await signup.Content.ReadFromJsonAsync<AuthResponse>())!;
        UseToken(user.Token);
        try
        {
            var post = await _client.PostAsJsonAsync("/api/v1/configs/connections",
                BaseConnections(new ProviderConnection("anthropic", "https://api.anthropic.com", "k")));
            Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }
        finally
        {
            UseToken(_admin.Token);
        }
    }
}
