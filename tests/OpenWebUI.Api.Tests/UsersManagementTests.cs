using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes da gestão de usuários: listagem paginada, papéis, sessões OAuth e presença.</summary>
[TestFixture]
public class UsersManagementTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-umgmt-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = await SignUpAsync("Admin Umgmt", "admin@umgmt.local", "senha123");
        UseToken(_admin.Token);
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
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    [Test, Order(1)]
    public async Task ListarUsuarios_ComBusca_FiltraPorNomeOuEmail()
    {
        await SignUpAsync("Zelda Pereira", "zelda@umgmt.local", "senha123");
        await SignUpAsync("Mario Costa", "mario@umgmt.local", "senha123");
        UseToken(_admin.Token);

        var result = await _client.GetFromJsonAsync<JsonElement>(
            "/api/v1/users/?query=zelda");

        var users = result.GetProperty("users");
        Assert.That(result.GetProperty("total").GetInt32(), Is.EqualTo(1));
        Assert.That(users[0].GetProperty("email").GetString(), Is.EqualTo("zelda@umgmt.local"));
    }

    [Test, Order(2)]
    public async Task ListarUsuarios_ComFiltro_FiltraPorPapel()
    {
        UseToken(_admin.Token);

        var result = await _client.GetFromJsonAsync<JsonElement>(
            "/api/v1/users/?filter=admin");

        var users = result.GetProperty("users").EnumerateArray().ToList();
        Assert.That(users.Count, Is.GreaterThanOrEqualTo(1));
        Assert.That(users.All(u => u.GetProperty("role").GetString() == "admin"), Is.True);
    }

    [Test, Order(3)]
    public async Task ListarUsuarios_ComOrdenacao_E_Paginacao()
    {
        UseToken(_admin.Token);

        var ordered = await _client.GetFromJsonAsync<JsonElement>(
            "/api/v1/users/?order_by=name&direction=asc&per_page=100");
        var names = ordered.GetProperty("users").EnumerateArray()
            .Select(u => u.GetProperty("name").GetString()!).ToList();
        Assert.That(names, Is.Ordered);

        var total = ordered.GetProperty("total").GetInt32();
        var page1 = await _client.GetFromJsonAsync<JsonElement>(
            "/api/v1/users/?page=1&per_page=2");
        var page2 = await _client.GetFromJsonAsync<JsonElement>(
            "/api/v1/users/?page=2&per_page=2");

        Assert.That(page1.GetProperty("total").GetInt32(), Is.EqualTo(total));
        Assert.That(page1.GetProperty("page").GetInt32(), Is.EqualTo(1));
        Assert.That(page2.GetProperty("page").GetInt32(), Is.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(page1.GetProperty("users").GetArrayLength(), Is.EqualTo(2));
            Assert.That(page2.GetProperty("users").GetArrayLength(), Is.GreaterThanOrEqualTo(1));
        });

        var p1Ids = page1.GetProperty("users").EnumerateArray()
            .Select(u => u.GetProperty("id").GetString());
        var p2Ids = page2.GetProperty("users").EnumerateArray()
            .Select(u => u.GetProperty("id").GetString());
        Assert.That(p1Ids.Intersect(p2Ids), Is.Empty);
    }

    [Test, Order(4)]
    public async Task ListarUsuarios_UsuarioComum_RetornaProibido()
    {
        var user = await SignUpAsync("Comum", "comum@umgmt.local", "senha123");
        UseToken(user.Token);

        var response = await _client.GetAsync("/api/v1/users/?page=1");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test, Order(5)]
    public async Task AtivosSomenteAdmin_RetornaListaDeIds()
    {
        UseToken(_admin.Token);
        var result = await _client.GetFromJsonAsync<JsonElement>("/api/v1/users/active");
        Assert.That(result.TryGetProperty("ids", out _), Is.True);

        var user = await SignUpAsync("Ativo Teste", "ativo@umgmt.local", "senha123");
        UseToken(user.Token);
        var forbidden = await _client.GetAsync("/api/v1/users/active");
        Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test, Order(6)]
    public async Task AtualizarPapel_SomenteAdmin_IsoladoDeOutrosCampos()
    {
        var user = await SignUpAsync("Candidato", "candidato@umgmt.local", "senha123");

        UseToken(user.Token);
        var self = await _client.GetFromJsonAsync<UserResponse>("/api/v1/auths/");
        var denied = await _client.PostAsJsonAsync(
            $"/api/v1/users/{self!.Id}/update/role", new UpdateUserRoleRequest("admin"));
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        UseToken(_admin.Token);
        var invalid = await _client.PostAsJsonAsync(
            $"/api/v1/users/{self.Id}/update/role", new UpdateUserRoleRequest("superuser"));
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var notFound = await _client.PostAsJsonAsync(
            "/api/v1/users/us_naoexiste/update/role", new UpdateUserRoleRequest("admin"));
        Assert.That(notFound.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var ok = await _client.PostAsJsonAsync(
            $"/api/v1/users/{self.Id}/update/role", new UpdateUserRoleRequest("admin"));
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var updated = await ok.Content.ReadFromJsonAsync<UserResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(updated!.Role, Is.EqualTo("admin"));
            Assert.That(updated.Name, Is.EqualTo("Candidato"));
        });
    }

    [Test, Order(7)]
    public async Task SessoesOAuth_ProprioOuAdmin()
    {
        var user = await SignUpAsync("OAuth User", "oauthuser@umgmt.local", "senha123");
        UseToken(user.Token);
        var self = await _client.GetFromJsonAsync<UserResponse>("/api/v1/auths/");

        var own = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/users/{self!.Id}/oauth/sessions");
        Assert.That(own.TryGetProperty("sessions", out _), Is.True);

        var other = await SignUpAsync("Outro", "outro@umgmt.local", "senha123");
        UseToken(other.Token);
        var denied = await _client.GetAsync($"/api/v1/users/{self.Id}/oauth/sessions");
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        UseToken(_admin.Token);
        var asAdmin = await _client.GetAsync($"/api/v1/users/{self.Id}/oauth/sessions");
        Assert.That(asAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test, Order(8)]
    public async Task PostUserSettings_Alias_PersistePreferencias()
    {
        var user = await SignUpAsync("Prefs", "prefs@umgmt.local", "senha123");
        UseToken(user.Token);

        var post = await _client.PostAsJsonAsync("/api/v1/users/user/settings",
            new { theme = "dark", fontSize = 14 });
        Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var settings = await _client.GetFromJsonAsync<JsonElement>("/api/v1/users/user/settings");
        Assert.Multiple(() =>
        {
            Assert.That(settings.GetProperty("theme").GetString(), Is.EqualTo("dark"));
            Assert.That(settings.GetProperty("fontSize").GetInt32(), Is.EqualTo(14));
        });
    }

    [Test, Order(9)]
    public async Task Signup_UsuarioRecebePermissoesDefault_Permissivas()
    {
        var user = await SignUpAsync("Perm Default", "permdef@umgmt.local", "senha123");
        UseToken(user.Token);

        // O default permissivo permite acesso ao workspace sem grupo explícito.
        var prompts = await _client.GetAsync("/api/v1/prompts/");
        Assert.That(prompts.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test, Order(10)]
    public async Task ListarUsuarios_RetornaLastActiveAtComoUpdatedAt()
    {
        var user = await SignUpAsync("Ativo Campo", "ativocampo@umgmt.local", "senha123");
        UseToken(user.Token);
        var self = await _client.GetFromJsonAsync<UserResponse>("/api/v1/auths/");

        UseToken(_admin.Token);
        var result = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/users/?query=ativocampo");
        var entry = result.GetProperty("users")[0];
        Assert.That(entry.GetProperty("id").GetString(), Is.EqualTo(self!.Id));
        Assert.That(entry.GetProperty("lastActiveAt").GetInt64(), Is.GreaterThan(0));
    }
}
