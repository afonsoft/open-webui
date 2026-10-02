using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes de integração dos endpoints de autenticação administrativa e perfil.</summary>
[TestFixture]
public class AuthAdminTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-authadmin-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        // Garante LDAP desabilitado: sem servidor/template o TryBindAsync retorna null
        // e o signin cai para a autenticação local.
        Environment.SetEnvironmentVariable("LDAP_SERVER", null);
        Environment.SetEnvironmentVariable("LDAP_USER_DN_TEMPLATE", null);
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = await SignUpAsync("Admin", "admin@authadmin.local", "senha123");
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
    public async Task UpdatePassword_SenhaAtualErrada_Retorna400()
    {
        var user = await SignUpAsync("Pwd", "pwd@authadmin.local", "senha123");
        UseToken(user.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/auths/update/password",
            new UpdatePasswordRequest("errada", "nova456"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(2)]
    public async Task UpdatePassword_NovaSenhaCurta_Retorna400()
    {
        var user = await SignUpAsync("PwdShort", "pwdshort@authadmin.local", "senha123");
        UseToken(user.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/auths/update/password",
            new UpdatePasswordRequest("senha123", "abc"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(3)]
    public async Task UpdatePassword_TrocaComSucesso_PermiteLoginComNovaSenha()
    {
        var user = await SignUpAsync("PwdOk", "pwdok@authadmin.local", "senha123");
        UseToken(user.Token);

        var changed = await _client.PostAsJsonAsync("/api/v1/auths/update/password",
            new UpdatePasswordRequest("senha123", "nova456"));
        Assert.That(changed.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        _client.DefaultRequestHeaders.Authorization = null;
        var oldSignin = await _client.PostAsJsonAsync("/api/v1/auths/signin",
            new SignInRequest("pwdok@authadmin.local", "senha123"));
        Assert.That(oldSignin.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var newSignin = await _client.PostAsJsonAsync("/api/v1/auths/signin",
            new SignInRequest("pwdok@authadmin.local", "nova456"));
        Assert.That(newSignin.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var auth = (await newSignin.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.That(auth.Token, Is.Not.Empty);
    }

    [Test, Order(4)]
    public async Task UpdateProfile_AtualizaNomeEImagem()
    {
        var user = await SignUpAsync("Perfil", "perfil@authadmin.local", "senha123");
        UseToken(user.Token);

        var updated = await _client.PostAsJsonAsync("/api/v1/auths/update/profile",
            new UpdateProfileRequest("Novo Nome", "https://img.local/avatar.png"));
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var me = await _client.GetFromJsonAsync<UserResponse>("/api/v1/auths/");
        Assert.Multiple(() =>
        {
            Assert.That(me!.Name, Is.EqualTo("Novo Nome"));
            Assert.That(me.ProfileImageUrl, Is.EqualTo("https://img.local/avatar.png"));
        });
    }

    [Test, Order(5)]
    public async Task UpdateTimezone_PersisteFuso()
    {
        var user = await SignUpAsync("Tz", "tz@authadmin.local", "senha123");
        UseToken(user.Token);

        var updated = await _client.PostAsJsonAsync("/api/v1/auths/update/timezone",
            new UpdateTimezoneRequest("America/Sao_Paulo"));
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var me = await _client.GetFromJsonAsync<UserResponse>("/api/v1/auths/");
        Assert.That(me!.Timezone, Is.EqualTo("America/Sao_Paulo"));
    }

    [Test, Order(6)]
    public async Task GetApiKey_SemChave_404_EAposCriar_RetornaInfo()
    {
        var user = await SignUpAsync("KeyInfo", "keyinfo@authadmin.local", "senha123");
        UseToken(user.Token);

        var missing = await _client.GetAsync("/api/v1/auths/api_key");
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var created = await _client.PostAsync("/api/v1/auths/api_key", null);
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var info = await _client.GetFromJsonAsync<ApiKeyInfoResponse>("/api/v1/auths/api_key");
        Assert.That(info, Is.Not.Null);
        Assert.That(info!.CreatedAt, Is.GreaterThan(0));
    }

    [Test, Order(7)]
    public async Task AddUser_Admin_CriaUsuarioQueConsegueLogar()
    {
        UseToken(_admin.Token);

        var created = await _client.PostAsJsonAsync("/api/v1/auths/add",
            new AddUserRequest("Criado", "criado@authadmin.local", "senha123", "user"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var user = (await created.Content.ReadFromJsonAsync<UserResponse>())!;
        Assert.Multiple(() =>
        {
            Assert.That(user.Role, Is.EqualTo("user"));
            Assert.That(user.Email, Is.EqualTo("criado@authadmin.local"));
        });

        _client.DefaultRequestHeaders.Authorization = null;
        var signin = await _client.PostAsJsonAsync("/api/v1/auths/signin",
            new SignInRequest("criado@authadmin.local", "senha123"));
        Assert.That(signin.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test, Order(8)]
    public async Task AddUser_UsuarioComum_Retorna403()
    {
        var user = await SignUpAsync("Comum", "comum@authadmin.local", "senha123");
        UseToken(user.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/auths/add",
            new AddUserRequest("Outro", "outro@authadmin.local", "senha123", "user"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test, Order(9)]
    public async Task AdminConfig_AdminVe_UsuarioComum403()
    {
        UseToken(_admin.Token);
        var adminView = await _client.GetAsync("/api/v1/auths/admin/config");
        Assert.That(adminView.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var config = await adminView.Content.ReadFromJsonAsync<AdminConfig>();
        Assert.That(config!.DefaultUserRole, Is.EqualTo("user"));

        var user = await SignUpAsync("Cfg", "cfg@authadmin.local", "senha123");
        UseToken(user.Token);
        var userView = await _client.GetAsync("/api/v1/auths/admin/config");
        Assert.That(userView.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test, Order(10)]
    public async Task LdapDesabilitado_SigninDesconhecido_CaiParaAuthLocal()
    {
        // LDAP desabilitado: TryBindAsync retorna null e o signin de e-mail
        // inexistente devolve erro genérico de credenciais locais.
        _client.DefaultRequestHeaders.Authorization = null;

        var unknown = await _client.PostAsJsonAsync("/api/v1/auths/signin",
            new SignInRequest("naoexiste@authadmin.local", "senha123"));
        Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var user = await SignUpAsync("Local", "local@authadmin.local", "senha123");
        var wrongPass = await _client.PostAsJsonAsync("/api/v1/auths/signin",
            new SignInRequest("local@authadmin.local", "errada"));
        Assert.That(wrongPass.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var rightPass = await _client.PostAsJsonAsync("/api/v1/auths/signin",
            new SignInRequest("local@authadmin.local", "senha123"));
        Assert.That(rightPass.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}
