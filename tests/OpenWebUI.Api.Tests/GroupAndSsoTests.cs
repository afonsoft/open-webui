using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes da slice auth-sso-rbac: grupos, permissões e vínculo OAuth.</summary>
[TestFixture, IsolateEnvironment]
public class GroupAndSsoTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;
    private AuthResponse _user = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-sso-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("GITHUB_CLIENT_ID", "gh-test-id");
        Environment.SetEnvironmentVariable("GITHUB_CLIENT_SECRET", "gh-test-secret");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = await SignUpAsync("Admin", "admin@test.local", "senha123");
        UseToken(_admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = null;
        _user = await SignUpAsync("User", "user@test.local", "senha123");
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

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    [Test, Order(1)]
    // Covers RF-003: somente admin global cria grupos.
    public async Task Groups_SomenteAdminCria()
    {
        UseToken(_user.Token);
        var denied = await _client.PostAsJsonAsync("/api/v1/groups",
            new CreateGroupRequest("Engenharia", null, null));
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
            await denied.Content.ReadAsStringAsync());

        UseToken(_admin.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/groups",
            new CreateGroupRequest("Engenharia", "Time de eng", null));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var group = await created.Content.ReadFromJsonAsync<GroupResponse>();
        Assert.That(group!.Name, Is.EqualTo("Engenharia"));

        var list = await _client.GetFromJsonAsync<List<GroupResponse>>("/api/v1/groups");
        Assert.That(list!.Count, Is.EqualTo(1));
    }

    [Test, Order(2)]
    // Covers RF-003/RF-004: membro em grupo sem sharing.public_chats recebe 403 no share;
    // removido do grupo, volta ao default permitido.
    public async Task Permissions_GrupoSemSharing_BloqueiaShare()
    {
        UseToken(_admin.Token);
        var permissions = new GroupPermissions(
            Workspace: new WorkspacePermissions(true, true, true, true, true),
            Sharing: new SharingPermissions(false),
            Chat: new ChatPermissions(true));
        var created = await _client.PostAsJsonAsync("/api/v1/groups",
            new CreateGroupRequest("SemShare", null, permissions));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var group = await created.Content.ReadFromJsonAsync<GroupResponse>();

        var add = await _client.PostAsJsonAsync($"/api/v1/groups/{group!.Id}/members",
            new UpdateGroupMembersRequest([_user.User.Id]));
        Assert.That(add.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await add.Content.ReadAsStringAsync());

        UseToken(_user.Token);
        var chat = await _client.PostAsJsonAsync("/api/v1/chats",
            new ChatUpsertRequest("chat bloqueado", [], []));
        Assert.That(chat.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var chatId = (await chat.Content.ReadFromJsonAsync<ChatResponse>())!.Id;

        var denied = await _client.PostAsync($"/api/v1/chats/{chatId}/share", null);
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
            await denied.Content.ReadAsStringAsync());

        UseToken(_admin.Token);
        var remove = await _client.DeleteAsync($"/api/v1/groups/{group.Id}/members/{_user.User.Id}");
        Assert.That(remove.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        UseToken(_user.Token);
        var allowed = await _client.PostAsync($"/api/v1/chats/{chatId}/share", null);
        Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await allowed.Content.ReadAsStringAsync());
    }

    [Test, Order(3)]
    // Covers RF-001: conta OAuth nova cria usuário com papel default; e-mail
    // existente vincula a conta OAuth em vez de duplicar.
    public async Task OAuth_VinculaOuCriaUsuario()
    {
        await using var db = CreateContext();
        using var mc1 = new MemoryCache(new MemoryCacheOptions());
        var oauth = new OAuthService(db, new ConfigService(db, mc1));

        var linked = await oauth.LinkOrCreateAsync(
            "github", "gh-42", "user@test.local", "User GH");
        Assert.Multiple(() =>
        {
            Assert.That(linked.UserId, Is.EqualTo(_user.User.Id));
            Assert.That(linked.NewUser, Is.False);
        });

        var created = await oauth.LinkOrCreateAsync(
            "github", "gh-99", "novo@test.local", "Novo OAuth");
        var user = await db.Users.FindAsync(created.UserId);
        Assert.Multiple(() =>
        {
            Assert.That(created.NewUser, Is.True);
            Assert.That(user!.Role, Is.EqualTo("user"));
            Assert.That(user.PasswordHash, Is.Empty);
        });

        var accounts = await db.OAuthAccounts.ToListAsync();
        Assert.That(accounts.Count, Is.EqualTo(2));
    }

    [Test, Order(4)]
    // Covers RF-001: /api/config expõe os providers OAuth configurados.
    public async Task Config_ExpoeProvidersOAuth()
    {
        var config = await _client.GetFromJsonAsync<AppConfigResponse>("/api/config");
        Assert.That(config!.OAuthProviders, Does.Contain("github"));
    }
}
