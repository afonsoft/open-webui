using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes de integração dos endpoints utilitários (/api/v1/utils).</summary>
[TestFixture]
[NonParallelizable]
public class UtilsEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-utils-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var signup = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new SignUpRequest("Admin", "admin@utils.local", "senha123"));
        Assert.That(signup.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await signup.Content.ReadAsStringAsync());
        _admin = (await signup.Content.ReadFromJsonAsync<AuthResponse>())!;
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        UseToken(_admin.Token);
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", null);
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<AuthResponse> SignUpAsync(string name, string email, string password)
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var response = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new SignUpRequest(name, email, password));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    [Test, Order(1)]
    public async Task Gravatar_RetornaUrlSha256Upstream()
    {
        UseToken(_admin.Token);

        var response = await _client.GetAsync("/api/v1/utils/gravatar?email=%20Admin%40Utils.LOCAL%20");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var url = await response.Content.ReadAsStringAsync();
        // Upstream: SHA256 do e-mail trim+lowercase, sufixo ?d=mp.
        var expected = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("admin@utils.local"))).ToLowerInvariant();
        Assert.That(url, Does.Contain($"/avatar/{expected}"));
        Assert.That(url, Does.Contain("?d=mp"));
    }

    [Test, Order(2)]
    public async Task Gravatar_SemEmail_Retorna400()
    {
        UseToken(_admin.Token);
        var response = await _client.GetAsync("/api/v1/utils/gravatar");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(3)]
    public async Task CodeFormat_Json_FormataComIndentacao()
    {
        UseToken(_admin.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/utils/code/format",
            new CodeFormatRequest("{\"a\":1,\"b\":[1,2]}", "json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var formatted = body.GetProperty("code").GetString()!;
        Assert.That(formatted, Does.Contain("\n"));
        Assert.That(formatted, Does.Contain("\"a\": 1"));
    }

    [Test, Order(4)]
    public async Task CodeFormat_LinguagemSemExecutor_Retorna501()
    {
        UseToken(_admin.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/utils/code/format",
            new CodeFormatRequest("print('oi')", "python"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotImplemented));
    }

    [Test, Order(5)]
    public async Task CodeFormat_JsonInvalido_Retorna400()
    {
        UseToken(_admin.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/utils/code/format",
            new CodeFormatRequest("{nao-json", "json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(6)]
    public async Task CodeFormat_UsuarioComum_Retorna403()
    {
        var user = await SignUpAsync("Comum", "comum@utils.local", "senha123");
        UseToken(user.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/utils/code/format",
            new CodeFormatRequest("{\"a\":1}", "json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test, Order(7)]
    public async Task Gravatar_UsuarioComum_Retorna200()
    {
        // Gravatar fica disponível a qualquer usuário verificado (get_verified_user).
        var user = await SignUpAsync("Comum2", "comum2@utils.local", "senha123");
        UseToken(user.Token);

        var response = await _client.GetAsync("/api/v1/utils/gravatar?email=comum2@utils.local");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}
