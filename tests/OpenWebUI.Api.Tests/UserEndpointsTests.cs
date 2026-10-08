using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes de integração dos endpoints administrativos e de preferências de usuários.</summary>
[TestFixture]
public class UserEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-users-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = await SignUpAsync("Admin", "admin@users.local", "senha123");
        UseToken(_admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
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
    public async Task ListarUsuarios_Admin_RetornaListaComTotal()
    {
        var extra = await SignUpAsync("Listado", "listado@users.local", "senha123");
        UseToken(_admin.Token);

        var result = await _client.GetFromJsonAsync<JsonElement>("/api/v1/users/");

        var total = result.GetProperty("total").GetInt32();
        var users = result.GetProperty("users");
        Assert.Multiple(() =>
        {
            Assert.That(total, Is.EqualTo(users.GetArrayLength()));
            Assert.That(total, Is.GreaterThanOrEqualTo(2));
        });
        var emails = users.EnumerateArray().Select(u => u.GetProperty("email").GetString()).ToList();
        Assert.That(emails, Does.Contain("listado@users.local"));
    }

    [Test, Order(2)]
    public async Task ListarUsuarios_UsuarioComum_Retorna403()
    {
        var user = await SignUpAsync("Comum", "comum@users.local", "senha123");
        UseToken(user.Token);

        var response = await _client.GetAsync("/api/v1/users/");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test, Order(3)]
    public async Task ListarTodos_Admin_RetornaArrayDeUsuarios()
    {
        UseToken(_admin.Token);

        var all = await _client.GetFromJsonAsync<List<UserResponse>>("/api/v1/users/all");

        Assert.That(all, Is.Not.Null.And.Not.Empty);
        Assert.That(all!.Any(u => u.Email == "admin@users.local"), Is.True);
    }

    [Test, Order(4)]
    public async Task BuscarUsuarios_FiltraPorNomeOuEmail()
    {
        await SignUpAsync("BuscaAlvo", "alvo.buscado@users.local", "senha123");
        var user = await SignUpAsync("Buscador", "buscador@users.local", "senha123");
        UseToken(user.Token);

        var found = await _client.GetFromJsonAsync<List<JsonElement>>(
            "/api/v1/users/search?query=buscaalvo");

        Assert.That(found, Has.Count.EqualTo(1));
        Assert.That(found![0].GetProperty("email").GetString(),
            Is.EqualTo("alvo.buscado@users.local"));
    }

    [Test, Order(5)]
    public async Task GetUser_Admin_PorId_RetornaUsuario_IdInexistente_404()
    {
        var alvo = await SignUpAsync("Alvo", "alvo@users.local", "senha123");
        UseToken(_admin.Token);

        var found = await _client.GetFromJsonAsync<UserResponse>($"/api/v1/users/{alvo.User.Id}");
        Assert.That(found!.Email, Is.EqualTo("alvo@users.local"));

        var missing = await _client.GetAsync("/api/v1/users/id-inexistente");
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test, Order(6)]
    public async Task UpdateUser_Admin_AtualizaNomeEImagem()
    {
        var alvo = await SignUpAsync("Editavel", "editavel@users.local", "senha123");
        UseToken(_admin.Token);

        var updated = await _client.PostAsJsonAsync($"/api/v1/users/{alvo.User.Id}/update",
            new AdminUpdateUserRequest("Nome Editado", null, null, "https://img.local/e.png"));
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var user = (await updated.Content.ReadFromJsonAsync<UserResponse>())!;
        Assert.Multiple(() =>
        {
            Assert.That(user.Name, Is.EqualTo("Nome Editado"));
            Assert.That(user.ProfileImageUrl, Is.EqualTo("https://img.local/e.png"));
        });
    }

    [Test, Order(7)]
    public async Task UpdateUser_PromoveParaAdmin_NovoTokenTemAcessoAdmin()
    {
        var alvo = await SignUpAsync("Promovido", "promovido@users.local", "senha123");
        UseToken(_admin.Token);

        var promoted = await _client.PostAsJsonAsync($"/api/v1/users/{alvo.User.Id}/update",
            new AdminUpdateUserRequest(null, "admin", null, null));
        Assert.That(promoted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var updated = (await promoted.Content.ReadFromJsonAsync<UserResponse>())!;
        Assert.That(updated.Role, Is.EqualTo("admin"));

        // O role vai no JWT: só o novo login reflete a promoção.
        _client.DefaultRequestHeaders.Authorization = null;
        var signin = await _client.PostAsJsonAsync("/api/v1/auths/signin",
            new SignInRequest("promovido@users.local", "senha123"));
        var auth = (await signin.Content.ReadFromJsonAsync<AuthResponse>())!;
        UseToken(auth.Token);
        var adminView = await _client.GetAsync("/api/v1/users/");
        Assert.That(adminView.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test, Order(8)]
    public async Task UpdateUser_UsuarioComum_Retorna403()
    {
        var alvo = await SignUpAsync("Vitima", "vitima@users.local", "senha123");
        var comum = await SignUpAsync("SemPermissao", "semperm@users.local", "senha123");
        UseToken(comum.Token);

        var response = await _client.PostAsJsonAsync($"/api/v1/users/{alvo.User.Id}/update",
            new AdminUpdateUserRequest("Hackeado", "admin", null, null));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test, Order(9)]
    public async Task DeleteUser_Admin_RemoveUsuarioQueNaoLogaMais()
    {
        var alvo = await SignUpAsync("Deletavel", "deletavel@users.local", "senha123");
        UseToken(_admin.Token);

        var deleted = await _client.DeleteAsync($"/api/v1/users/{alvo.User.Id}");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var gone = await _client.GetAsync($"/api/v1/users/{alvo.User.Id}");
        Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        _client.DefaultRequestHeaders.Authorization = null;
        var signin = await _client.PostAsJsonAsync("/api/v1/auths/signin",
            new SignInRequest("deletavel@users.local", "senha123"));
        Assert.That(signin.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(10)]
    public async Task DeleteUser_PropriaConta_Retorna400()
    {
        UseToken(_admin.Token);

        var response = await _client.DeleteAsync($"/api/v1/users/{_admin.User.Id}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(11)]
    public async Task DeleteUser_UsuarioComum_Retorna403()
    {
        var alvo = await SignUpAsync("AlvoDel", "alvodel@users.local", "senha123");
        var comum = await SignUpAsync("DelComum", "delcomum@users.local", "senha123");
        UseToken(comum.Token);

        var response = await _client.DeleteAsync($"/api/v1/users/{alvo.User.Id}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test, Order(12)]
    public async Task Permissoes_RetornaEstruturaPadrao()
    {
        var user = await SignUpAsync("Perms", "perms@users.local", "senha123");
        UseToken(user.Token);

        var perms = await _client.GetFromJsonAsync<JsonElement>("/api/v1/users/permissions");

        Assert.Multiple(() =>
        {
            Assert.That(perms.GetProperty("workspace").GetProperty("models").GetBoolean(), Is.True);
            Assert.That(perms.GetProperty("chat").GetProperty("stt").GetBoolean(), Is.True);
            Assert.That(perms.GetProperty("features").GetProperty("notes").GetBoolean(), Is.True);
        });
    }

    [Test, Order(13)]
    public async Task UserSettings_SalvaERecupera()
    {
        var user = await SignUpAsync("Settings", "settings@users.local", "senha123");
        UseToken(user.Token);

        var saved = await _client.PostAsJsonAsync("/api/v1/users/user/settings/update",
            new { ui = new { theme = "dark", lang = "pt-BR" } });
        Assert.That(saved.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var settings = await _client.GetFromJsonAsync<JsonElement>("/api/v1/users/user/settings");
        Assert.Multiple(() =>
        {
            Assert.That(settings.GetProperty("ui").GetProperty("theme").GetString(),
                Is.EqualTo("dark"));
            Assert.That(settings.GetProperty("ui").GetProperty("lang").GetString(),
                Is.EqualTo("pt-BR"));
        });
    }
}
