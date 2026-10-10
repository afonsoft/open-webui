using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes de permissões granulares (SPEC permissions-granular):
/// override por usuário vencendo o grupo, endpoint admin de permissões
/// e membership automático por domínio de e-mail no signup.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class PermissionsGranularTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-perm-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest("Admin", "admin@perm.local", "senha123"));
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

    private void UseToken(string? token) =>
        _client.DefaultRequestHeaders.Authorization = token is null
            ? null
            : new AuthenticationHeaderValue("Bearer", token);

    private async Task<AuthResponse> SignUpAsync(string name, string email)
    {
        UseToken(null);
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, "senha123"));
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    /// <summary>Permissões negando apenas a flag indicada.</summary>
    private static GroupPermissions NegandoWorkspace(string flag)
    {
        var ws = new WorkspacePermissions(
            Models: flag != "models", Prompts: flag != "prompts",
            Knowledge: flag != "knowledge", Tools: flag != "tools",
            Files: flag != "files");
        return new GroupPermissions(ws, new SharingPermissions(), new ChatPermissions());
    }

    private async Task<GroupResponse> CreateGroupWithAsync(
        string name, GroupPermissions perms, string[]? domains, string userId)
    {
        UseToken(_admin.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/groups",
            new CreateGroupRequest(name, null, perms, domains));
        var group = (await created.Content.ReadFromJsonAsync<GroupResponse>())!;
        await _client.PostAsJsonAsync($"/api/v1/groups/{group.Id}/members",
            new UpdateGroupMembersRequest([userId]));
        return group;
    }

    /// <summary>Verifica se o usuário pode listar workspaces models (usa a permissão em rota real não é direta;
    /// o merge é testado via PermissionService no arquivo próprio; aqui testa-se a API).</summary>
    private static JsonDocument JsonObj(string json) => JsonDocument.Parse(json);

    // ----------------- Override por usuário -----------------

    [Test]
    public async Task Override_Usuario_Vence_Grupo_Na_Verificacao_Do_Servico()
    {
        // Grupo que nega workspace.models; override do usuário permite de volta.
        var user = await SignUpAsync("U1", "u1@perm.local");
        await CreateGroupWithAsync("g-restrita", NegandoWorkspace("models"), null, user.User.Id);

        UseToken(_admin.Token);
        var put = await _client.PutAsJsonAsync(
            $"/api/v1/users/{user.User.Id}/permissions",
            JsonObj("""{"workspace":{"models":true}}""").RootElement);
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // A permissão efetiva aparece em /api/v1/users/{id}/permissions (o próprio JSON salvo).
        var get = await _client.GetAsync($"/api/v1/users/{user.User.Id}/permissions");
        var doc = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Multiple(() =>
        {
            Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(doc.GetProperty("workspace").GetProperty("models").GetBoolean(), Is.True);
        });
    }

    [Test]
    public async Task Put_Permissoes_NaoAdmin_403()
    {
        var user = await SignUpAsync("U2", "u2@perm.local");
        UseToken(user.Token);
        var put = await _client.PutAsJsonAsync(
            $"/api/v1/users/{user.User.Id}/permissions",
            JsonObj("""{"workspace":{"models":false}}""").RootElement);
        var get = await _client.GetAsync($"/api/v1/users/{user.User.Id}/permissions");
        Assert.Multiple(() =>
        {
            Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        });
    }

    [Test]
    public async Task Put_Permissoes_Valida_Objeto()
    {
        var user = await SignUpAsync("U3", "u3@perm.local");
        UseToken(_admin.Token);
        // Array não é objeto → 400.
        var bad = await _client.PutAsJsonAsync(
            $"/api/v1/users/{user.User.Id}/permissions",
            JsonObj("""[1,2]""").RootElement);
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Put_Permissoes_UsuarioInexistente_404()
    {
        UseToken(_admin.Token);
        var put = await _client.PutAsJsonAsync(
            "/api/v1/users/nao-existe/permissions",
            JsonObj("""{"workspace":{"models":false}}""").RootElement);
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ----------------- Membership por domínio -----------------

    [Test]
    public async Task Signup_Dominio_EntraNoGrupo_Automaticamente()
    {
        UseToken(_admin.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/groups",
            new CreateGroupRequest("corp", null, null, ["corp.test"]));
        var group = (await created.Content.ReadFromJsonAsync<GroupResponse>())!;

        var user = await SignUpAsync("Dominio", "alguem@corp.test");

        UseToken(_admin.Token);
        var corp = await _client.GetFromJsonAsync<GroupResponse>($"/api/v1/groups/{group.Id}");
        Assert.That(corp!.Members.Any(m => m.UserId == user.User.Id), Is.True);
    }

    [Test]
    public async Task Signup_SemDominio_NaoEntraEmGrupo()
    {
        var user = await SignUpAsync("Fora", "fora@perm.local");

        UseToken(_admin.Token);
        var list = await _client.GetFromJsonAsync<List<GroupResponse>>("/api/v1/groups");
        Assert.That(list!.All(g => g.Members.All(m => m.UserId != user.User.Id)), Is.True);
    }

    // ----------------- Enforcement nos endpoints -----------------

    [Test]
    public async Task Permissao_Negada_Bloqueia_Endpoints_Workspace()
    {
        var user = await SignUpAsync("U8", "u8@perm.local");
        UseToken(_admin.Token);
        var put = await _client.PutAsJsonAsync(
            $"/api/v1/users/{user.User.Id}/permissions",
            JsonObj("""{"workspace":{"knowledge":false}}""").RootElement);
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        UseToken(user.Token);
        Assert.That((await _client.GetAsync("/api/v1/knowledge")).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await _client.PostAsJsonAsync("/api/v1/knowledge",
                new CreateKnowledgeRequest("x", null))).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden));

        UseToken(_admin.Token);
        Assert.That((await _client.GetAsync("/api/v1/knowledge")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));

        // Re-habilitando, o acesso volta.
        await _client.PutAsJsonAsync(
            $"/api/v1/users/{user.User.Id}/permissions",
            JsonObj("""{"workspace":{"knowledge":true}}""").RootElement);
        UseToken(user.Token);
        Assert.That((await _client.GetAsync("/api/v1/knowledge")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Grupo_Update_Domains_Substitui()
    {
        UseToken(_admin.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/groups",
            new CreateGroupRequest("dom2", null, null, ["a.test", "b.test"]));
        var group = (await created.Content.ReadFromJsonAsync<GroupResponse>())!;
        Assert.That(group.AllowedDomains, Is.EquivalentTo(new[] { "a.test", "b.test" }));

        var put = await _client.PutAsJsonAsync($"/api/v1/groups/{group.Id}",
            new UpdateGroupRequest(null, null, null, ["c.test"]));
        var updated = (await put.Content.ReadFromJsonAsync<GroupResponse>())!;
        Assert.That(updated.AllowedDomains, Is.EqualTo(new[] { "c.test" }));
    }
}
