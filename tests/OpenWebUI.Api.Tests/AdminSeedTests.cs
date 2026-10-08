using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes do seed de usuário admin via variáveis ADMIN_* (primeiro boot).</summary>
[TestFixture]
public class AdminSeedTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-adminseed-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("ADMIN_NAME", "Seed Admin");
        Environment.SetEnvironmentVariable("ADMIN_EMAIL", "seed-admin@adminseed.local");
        Environment.SetEnvironmentVariable("ADMIN_PASSWORD", "s3nh4-s3ed");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("ADMIN_NAME", null);
        Environment.SetEnvironmentVariable("ADMIN_EMAIL", null);
        Environment.SetEnvironmentVariable("ADMIN_PASSWORD", null);
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Test, Order(1)]
    public async Task SignIn_AdminSemeadoPorEnv_AutenticaComoAdmin()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auths/signin",
            new SignInRequest("seed-admin@adminseed.local", "s3nh4-s3ed"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.That(auth.User.Role, Is.EqualTo("admin"));
        Assert.That(auth.User.Name, Is.EqualTo("Seed Admin"));
        Assert.That(auth.Token, Is.Not.Empty);
    }

    [Test, Order(2)]
    public async Task SignUp_AposSeed_NaoViraAdmin()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new SignUpRequest("Segundo", "segundo@adminseed.local", "senha123"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.That(auth.User.Role, Is.Not.EqualTo("admin"));
    }

    [Test, Order(3)]
    public async Task SignIn_AdminSemeado_SenhaErrada_Retorna400()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auths/signin",
            new SignInRequest("seed-admin@adminseed.local", "errada"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}
