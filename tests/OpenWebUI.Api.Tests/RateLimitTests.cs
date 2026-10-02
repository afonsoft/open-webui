using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes de rate limiting e lockout de login (SPEC rate-limiting):
/// lockout após falhas consecutivas, janela por usuário em completions
/// e config administrativa restrita a admin.
/// </summary>
[TestFixture]
[NonParallelizable]
public class RateLimitTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-rl-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest("Admin", "admin@rl.local", "senha123"));
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
            File.Delete(_dbPath);
        }
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private Task<HttpResponseMessage> SignInAsync(string email, string password)
    {
        _client.DefaultRequestHeaders.Authorization = null;
        return _client.PostAsJsonAsync("/api/v1/auths/signin", new SignInRequest(email, password));
    }

    private static RateLimitConfig Config(bool enabled, int permit = 60, int maxFailures = 5) =>
        new(enabled, permit, 60, maxFailures, 300);

    [Test, Order(1)]
    public async Task Config_GetERespostaPadrao()
    {
        UseToken(_admin.Token);
        var get = await _client.GetFromJsonAsync<RateLimitConfig>("/api/v1/configs/ratelimit");
        Assert.That(get, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(get!.Enabled, Is.False, "limite deve vir desligado por padrão");
            Assert.That(get.PermitLimit, Is.EqualTo(60));
            Assert.That(get.LoginMaxFailures, Is.EqualTo(5));
        });
    }

    [Test, Order(2)]
    public async Task Config_SomenteAdminAltera()
    {
        var signup = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest("User", "user@rl.local", "senha123"));
        var user = (await signup.Content.ReadFromJsonAsync<AuthResponse>())!;
        UseToken(user.Token);

        var update = await _client.PostAsJsonAsync("/api/v1/configs/ratelimit", Config(true));
        Assert.That(update.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        var reset = await _client.PostAsJsonAsync("/api/v1/configs/ratelimit/reset",
            new LoginLockoutResetRequest("user@rl.local"));
        Assert.That(reset.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test, Order(3)]
    public async Task Login_BloqueiaAposMaximoDeFalhas()
    {
        UseToken(_admin.Token);
        await _client.PostAsJsonAsync("/api/v1/configs/ratelimit", Config(false, maxFailures: 3));
        await _client.PostAsJsonAsync("/api/v1/auths/add",
            new AddUserRequest("Vitima", "vitima@rl.local", "senha123", "user"));

        for (var i = 0; i < 3; i++)
        {
            var fail = await SignInAsync("vitima@rl.local", "errada");
            Assert.That(fail.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), $"tentativa {i + 1}");
        }

        // 4ª tentativa — mesmo com a senha correta — responde 429 pelo lockout.
        var locked = await SignInAsync("vitima@rl.local", "senha123");
        Assert.That(locked.StatusCode, Is.EqualTo((HttpStatusCode)429));
    }

    [Test, Order(4)]
    public async Task Login_ResetAdminLiberaBloqueio()
    {
        UseToken(_admin.Token);
        var reset = await _client.PostAsJsonAsync("/api/v1/configs/ratelimit/reset",
            new LoginLockoutResetRequest("vitima@rl.local"));
        Assert.That(reset.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var ok = await SignInAsync("vitima@rl.local", "senha123");
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test, Order(5)]
    public async Task Completions_SemLimiteQuandoDesabilitado()
    {
        var user = (await (await _client.PostAsJsonAsync(
                "/api/v1/auths/signup", new SignUpRequest("Free", "free@rl.local", "senha123")))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        UseToken(user.Token);

        for (var i = 0; i < 5; i++)
        {
            var response = await _client.PostAsJsonAsync("/api/chat/completions",
                new { model = "x", messages = new[] { new { role = "user", content = "oi" } } });
            Assert.That((int)response.StatusCode, Is.Not.EqualTo(429), $"requisição {i + 1}");
        }
    }

    [Test, Order(6)]
    public async Task Completions_LimitaComRetryAfterQuandoHabilitado()
    {
        UseToken(_admin.Token);
        await _client.PostAsJsonAsync("/api/v1/configs/ratelimit", Config(true, permit: 2));

        var user = (await (await _client.PostAsJsonAsync(
                "/api/v1/auths/signup", new SignUpRequest("Ltd", "ltd@rl.local", "senha123")))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        UseToken(user.Token);

        var body = new { model = "x", messages = new[] { new { role = "user", content = "oi" } } };
        var first = await _client.PostAsJsonAsync("/api/chat/completions", body);
        var second = await _client.PostAsJsonAsync("/api/chat/completions", body);
        var third = await _client.PostAsJsonAsync("/api/chat/completions", body);

        Assert.Multiple(() =>
        {
            Assert.That((int)first.StatusCode, Is.Not.EqualTo(429));
            Assert.That((int)second.StatusCode, Is.Not.EqualTo(429));
            Assert.That((int)third.StatusCode, Is.EqualTo(429));
            Assert.That(third.Headers.RetryAfter?.Delta?.TotalSeconds, Is.GreaterThan(0));
        });
    }

    [Test, Order(7)]
    public async Task Completions_JanelaEIndependentePorUsuario()
    {
        // Outro usuário tem janela própria — não herda o limite consumido por ltd@.
        var user = (await (await _client.PostAsJsonAsync(
                "/api/v1/auths/signup", new SignUpRequest("Outro", "outro@rl.local", "senha123")))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        UseToken(user.Token);

        var response = await _client.PostAsJsonAsync("/api/chat/completions",
            new { model = "x", messages = new[] { new { role = "user", content = "oi" } } });
        Assert.That((int)response.StatusCode, Is.Not.EqualTo(429));
    }
}
