using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes dos endpoints de configuração por domínio (/api/v1/configs) e banners.</summary>
[TestFixture, IsolateEnvironment]
public class ConfigV2Tests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-cfgv2-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest("Admin", "admin@cfgv2.local", "senha123"));
        _admin = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;

        UseToken(_admin.Token);
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
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<AuthResponse> SignUpAsync(string name, string email)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, "senha123"));
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    [Test]
    public async Task Banners_CrudSomenteAdmin()
    {
        UseToken(_admin.Token);

        var created = await _client.PostAsJsonAsync("/api/v1/configs/banners",
            new BannerRequest("warning", "Manutenção", "Sistema em manutenção"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var banner = await created.Content.ReadFromJsonAsync<BannerResponse>();
        Assert.That(banner!.Type, Is.EqualTo("warning"));

        var list = await _client.GetFromJsonAsync<List<BannerResponse>>("/api/v1/configs/banners");
        Assert.That(list!.Count(b => b.Id == banner.Id), Is.EqualTo(1));

        var updated = await _client.PutAsJsonAsync($"/api/v1/configs/banners/{banner.Id}",
            new BannerRequest("info", "Aviso", "Atualizado"));
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var invalid = await _client.PostAsJsonAsync("/api/v1/configs/banners",
            new BannerRequest("urgente", "x", "y"));
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var deleted = await _client.DeleteAsync($"/api/v1/configs/banners/{banner.Id}");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var afterDelete = await _client.GetFromJsonAsync<List<BannerResponse>>("/api/v1/configs/banners");
        Assert.That(afterDelete!.Any(b => b.Id == banner.Id), Is.False);
    }

    [Test]
    public async Task Banners_UsuarioComum_LeMasNaoEscreve()
    {
        var user = await SignUpAsync("Comum", "comum@cfgv2.local");
        UseToken(user.Token);

        var read = await _client.GetAsync("/api/v1/configs/banners");
        Assert.That(read.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var write = await _client.PostAsJsonAsync("/api/v1/configs/banners",
            new BannerRequest("info", "x", "y"));
        Assert.That(write.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task ModelsConfig_PersisteDefaultModelsESugestoes()
    {
        UseToken(_admin.Token);

        var config = new ModelsConfig(
            ["llama3", "gpt-4o"],
            [new PromptSuggestion("Resumir", "Resuma o texto a seguir")]);
        var post = await _client.PostAsJsonAsync("/api/v1/configs/models", config);
        Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var get = await _client.GetFromJsonAsync<ModelsConfig>("/api/v1/configs/models");
        Assert.Multiple(() =>
        {
            Assert.That(get!.DefaultModels, Is.EqualTo(new[] { "llama3", "gpt-4o" }));
            Assert.That(get.PromptSuggestions[0].Title, Is.EqualTo("Resumir"));
        });

        // /api/config enriquecido expõe os defaults.
        var appConfig = await _client.GetFromJsonAsync<AppConfigResponse>("/api/config");
        Assert.That(appConfig!.DefaultModels, Does.Contain("llama3"));
        Assert.That(appConfig.DefaultPromptSuggestions.Select(s => s.Title), Does.Contain("Resumir"));
    }

    [Test]
    public async Task SignupToggle_Desabilitado_BloqueiaCadastro()
    {
        UseToken(_admin.Token);

        var set = await _client.PostAsJsonAsync("/api/v1/configs/signup",
            new SignupConfig(false, "pending"));
        Assert.That(set.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var signup = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new SignUpRequest("Bloqueado", "bloqueado@cfgv2.local", "senha123"));
        Assert.That(signup.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        // Restaura para os demais testes.
        await _client.PostAsJsonAsync("/api/v1/configs/signup", new SignupConfig(true, "user"));
    }

    [Test]
    public async Task ApiKeyToggle_Desabilitado_RejeitaChaveExistente()
    {
        UseToken(_admin.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/auths/api_key", new { });
        var apiKey = await created.Content.ReadFromJsonAsync<JsonElement>();
        var key = apiKey.GetProperty("apiKey").GetString()!;

        // Funciona antes de desabilitar.
        using var before = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auths/");
        before.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var beforeResponse = await _client.SendAsync(before);
        Assert.That(beforeResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await _client.PostAsJsonAsync("/api/v1/configs/api_key", new FeatureToggle(false));

        using var after = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auths/");
        after.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var afterResponse = await _client.SendAsync(after);
        Assert.That(afterResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        await _client.PostAsJsonAsync("/api/v1/configs/api_key", new FeatureToggle(true));
    }

    [Test]
    public async Task Toggles_E_CodeExecution_Roundtrip()
    {
        UseToken(_admin.Token);

        await _client.PostAsJsonAsync("/api/v1/configs/channels", new FeatureToggle(false));
        var channels = await _client.GetFromJsonAsync<FeatureToggle>("/api/v1/configs/channels");
        Assert.That(channels!.Enabled, Is.False);
        await _client.PostAsJsonAsync("/api/v1/configs/channels", new FeatureToggle(true));

        var code = new CodeExecutionConfig(["jupyter"], true);
        await _client.PostAsJsonAsync("/api/v1/configs/code_execution", code);
        var codeGet = await _client.GetFromJsonAsync<CodeExecutionConfig>("/api/v1/configs/code_execution");
        Assert.Multiple(() =>
        {
            Assert.That(codeGet!.Engines, Is.EqualTo(new[] { "jupyter" }));
            Assert.That(codeGet.DirectConnections, Is.True);
        });
        var direct = await _client.GetFromJsonAsync<FeatureToggle>("/api/v1/configs/direct_connections");
        Assert.That(direct!.Enabled, Is.True);
    }

    [Test]
    public async Task JwtConfig_Roundtrip_E_Validacao()
    {
        UseToken(_admin.Token);

        var invalid = await _client.PostAsJsonAsync("/api/v1/configs/jwt",
            new JwtExpiryConfig("quando-der"));
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var ok = await _client.PostAsJsonAsync("/api/v1/configs/jwt", new JwtExpiryConfig("12h"));
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var get = await _client.GetFromJsonAsync<JwtExpiryConfig>("/api/v1/configs/jwt");
        Assert.That(get!.ExpiresIn, Is.EqualTo("12h"));

        await _client.PostAsJsonAsync("/api/v1/configs/jwt", new JwtExpiryConfig("7d"));
    }

    [Test]
    public async Task Writes_UsuarioComum_RetornaProibido()
    {
        var user = await SignUpAsync("Sem Admin", "semadmin@cfgv2.local");
        UseToken(user.Token);

        var models = await _client.PostAsJsonAsync("/api/v1/configs/models", ModelsConfig.Empty);
        var signup = await _client.PostAsJsonAsync("/api/v1/configs/signup", new SignupConfig(true, "user"));
        var jwt = await _client.PostAsJsonAsync("/api/v1/configs/jwt", new JwtExpiryConfig("1h"));

        Assert.Multiple(() =>
        {
            Assert.That(models.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(signup.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(jwt.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        });
    }
}
