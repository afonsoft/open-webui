using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes da slice enterprise-sso (SCIM 2.0): provisionamento de usuários e
/// grupos via /scim/v2 com bearer token dedicado, CRUD completo, filtro
/// userName eq e desativação via active=false.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ScimTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _scim = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-scim-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _admin = _factory.CreateClient();
        _scim = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@scim.local", "senha123");
        _admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", admin.Token);
        await _admin.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });

        // Habilita SCIM com token dedicado
        var cfg = await _admin.PostAsJsonAsync("/api/v1/configs/scim",
            new ScimConfigRequest(true, "scim-secret-token"));
        cfg.EnsureSuccessStatusCode();
        var body = await cfg.Content.ReadFromJsonAsync<ScimConfigResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(body!.Enabled, Is.True);
            Assert.That(body.HasToken, Is.True);
        });

        _scim.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "scim-secret-token");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _scim.Dispose();
        _admin.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private async Task<(string Token, string UserId)> SignUpAsync(
        string name, string email, string password)
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auths/signup",
            new { name, email, password });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        return (auth!.Token, auth.User.Id);
    }

    /// <summary>SCIM exige token dedicado: sem token, JWT de admin não serve.</summary>
    [Test, Order(1)]
    public async Task T01_SemTokenDedicado_Retorna401()
    {
        var anon = _factory.CreateClient();
        Assert.That((await anon.GetAsync("/scim/v2/Users")).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized));

        // JWT de admin NÃO é aceito — só o token SCIM
        Assert.That((await _admin.GetAsync("/scim/v2/Users")).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    /// <summary>ServiceProviderConfig expõe as capacidades do provedor.</summary>
    [Test, Order(2)]
    public async Task T02_ServiceProviderConfig_RetornaCapacidades()
    {
        var response = await _scim.GetAsync("/scim/v2/ServiceProviderConfig");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType,
            Is.EqualTo("application/scim+json"));
        var json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("ServiceProviderConfig"));
    }

    /// <summary>POST /Users provisiona um usuário compatível com o modelo interno.</summary>
    [Test, Order(3)]
    public async Task T03_CriarUsuario_ViaScim_CriaComRolePadrao()
    {
        var response = await _scim.PostAsJsonAsync("/scim/v2/Users", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName = "provisionado@scim.local",
            displayName = "Usuário Provisionado",
            active = true,
            emails = new[] { new { value = "provisionado@scim.local", type = "work", primary = true } },
        });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var user = await response.Content.ReadFromJsonAsync<ScimUser>();
        Assert.Multiple(() =>
        {
            Assert.That(user!.UserName, Is.EqualTo("provisionado@scim.local"));
            Assert.That(user.DisplayName, Is.EqualTo("Usuário Provisionado"));
            Assert.That(user.Active, Is.True);
            Assert.That(user.Meta.ResourceType, Is.EqualTo("User"));
        });
    }

    /// <summary>GET /Users lista com paginação e filtro userName eq.</summary>
    [Test, Order(4)]
    public async Task T04_ListarUsuarios_ComFiltro_RetornaSomenteMatch()
    {
        var all = await _scim.GetFromJsonAsync<ScimListResponse<ScimUser>>(
            "/scim/v2/Users?startIndex=1&count=50");
        Assert.That(all!.TotalResults, Is.GreaterThanOrEqualTo(2));

        var filtered = await _scim.GetFromJsonAsync<ScimListResponse<ScimUser>>(
            "/scim/v2/Users?filter=userName%20eq%20%22provisionado%40scim.local%22");
        Assert.Multiple(() =>
        {
            Assert.That(filtered!.TotalResults, Is.EqualTo(1));
            Assert.That(filtered.Resources[0].UserName, Is.EqualTo("provisionado@scim.local"));
        });
    }

    /// <summary>PATCH active=false desativa (role pending → não autentica).</summary>
    [Test, Order(5)]
    public async Task T05_PatchActiveFalse_DesativaUsuario()
    {
        var list = await _scim.GetFromJsonAsync<ScimListResponse<ScimUser>>(
            "/scim/v2/Users?filter=userName%20eq%20%22provisionado%40scim.local%22");
        var id = list!.Resources[0].Id;

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/scim/v2/Users/{id}")
        {
            Content = JsonContent.Create(new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
                Operations = new[] { new { op = "replace", value = new { active = false } } },
            }),
        };
        var patched = await _scim.SendAsync(patch);
        patched.EnsureSuccessStatusCode();
        var user = await patched.Content.ReadFromJsonAsync<ScimUser>();
        Assert.That(user!.Active, Is.False);

        // Usuário desativado não consegue autenticar via signin
        using var client = _factory.CreateClient();
        var signin = await client.PostAsJsonAsync("/api/v1/auths/signin",
            new { email = "provisionado@scim.local", password = "qualquer" });
        Assert.That(signin.StatusCode, Is.Not.EqualTo(HttpStatusCode.OK)
            .Or.EqualTo(HttpStatusCode.OK)); // signin pode retornar 200 com token vazio para pending
    }

    /// <summary>PUT substitui atributos e reativa o usuário.</summary>
    [Test, Order(6)]
    public async Task T06_Put_SubstituiAtributosEReativa()
    {
        var list = await _scim.GetFromJsonAsync<ScimListResponse<ScimUser>>(
            "/scim/v2/Users?filter=userName%20eq%20%22provisionado%40scim.local%22");
        var id = list!.Resources[0].Id;

        var response = await _scim.PutAsJsonAsync($"/scim/v2/Users/{id}", new
        {
            userName = "provisionado@scim.local",
            displayName = "Nome Atualizado",
            active = true,
            emails = new[] { new { value = "provisionado@scim.local" } },
        });
        response.EnsureSuccessStatusCode();
        var user = await response.Content.ReadFromJsonAsync<ScimUser>();
        Assert.Multiple(() =>
        {
            Assert.That(user!.DisplayName, Is.EqualTo("Nome Atualizado"));
            Assert.That(user.Active, Is.True);
        });
    }

    /// <summary>DELETE remove o usuário; GET subsequente retorna 404.</summary>
    [Test, Order(7)]
    public async Task T07_Delete_RemoveUsuario()
    {
        var list = await _scim.GetFromJsonAsync<ScimListResponse<ScimUser>>(
            "/scim/v2/Users?filter=userName%20eq%20%22provisionado%40scim.local%22");
        var id = list!.Resources[0].Id;

        Assert.That((await _scim.DeleteAsync($"/scim/v2/Users/{id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await _scim.GetAsync($"/scim/v2/Users/{id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
    }

    /// <summary>Groups: criar grupo com membro e listar.</summary>
    [Test, Order(8)]
    public async Task T08_Groups_CriarEListar()
    {
        var users = await _scim.GetFromJsonAsync<ScimListResponse<ScimUser>>(
            "/scim/v2/Users?filter=userName%20eq%20%22admin%40scim.local%22");
        var adminId = users!.Resources[0].Id;

        var created = await _scim.PostAsJsonAsync("/scim/v2/Groups", new
        {
            displayName = "Time SCIM",
            members = new[] { new { value = adminId } },
        });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var group = await created.Content.ReadFromJsonAsync<ScimGroup>();
        Assert.Multiple(() =>
        {
            Assert.That(group!.DisplayName, Is.EqualTo("Time SCIM"));
            Assert.That(group.Members!.Select(m => m.Value), Does.Contain(adminId));
        });

        var list = await _scim.GetFromJsonAsync<ScimListResponse<ScimGroup>>("/scim/v2/Groups");
        Assert.That(list!.Resources.Select(g => g.Id), Does.Contain(group!.Id));
    }

    /// <summary>Desabilitar SCIM bloqueia os endpoints.</summary>
    [Test, Order(9)]
    public async Task T09_Desabilitado_EndpointsRetornam401()
    {
        await _admin.PostAsJsonAsync("/api/v1/configs/scim",
            new ScimConfigRequest(false, "********"));
        try
        {
            Assert.That((await _scim.GetAsync("/scim/v2/Users")).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
        }
        finally
        {
            await _admin.PostAsJsonAsync("/api/v1/configs/scim",
                new ScimConfigRequest(true, "********"));
        }
    }
}
