using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using OpenWebUI.Api.Endpoints;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura de endpoints pouco exercitados: api_key e updates de auths,
/// CRUD de knowledge/skills/functions/pipelines, models (access/import/export),
/// users (search/settings/permissions) e retrieval (config/reset/process).
/// </summary>
[TestFixture]
public class EndpointCoverageTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-ecov-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@ecov.local", "senha123");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
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
        _client.DefaultRequestHeaders.Authorization = null;
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, password));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    // ---------------- Auths: api_key + updates ----------------

    [Test]
    public async Task ApiKey_CicloCompleto()
    {
        var auth = await SignUpAsync("KeyUser", "key@ecov.local", "senha123");
        UseToken(auth.Token);

        Assert.That((await _client.GetAsync("/api/v1/auths/api_key")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));

        var created = await _client.PostAsync("/api/v1/auths/api_key", null);
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await created.Content.ReadFromJsonAsync<ApiKeyCreatedResponse>();
        Assert.That(body!.ApiKey, Does.StartWith("sk-"));

        Assert.That((await _client.GetAsync("/api/v1/auths/api_key")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));

        // A chave autentica como o usuário.
        UseToken(body.ApiKey);
        Assert.That((await _client.GetAsync("/api/v1/auths/")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));

        UseToken(auth.Token);
        Assert.That((await _client.DeleteAsync("/api/v1/auths/api_key")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.GetAsync("/api/v1/auths/api_key")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Auths_ProfileTimezonePassword_AddUser()
    {
        var auth = await SignUpAsync("Prof", "prof@ecov.local", "senha123");
        UseToken(auth.Token);

        Assert.That((await _client.PutAsJsonAsync("/api/v1/auths/profile",
            new UpdateProfileRequest("Novo Nome", null))).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PostAsJsonAsync("/api/v1/auths/update/timezone",
            new UpdateTimezoneRequest("America/Sao_Paulo"))).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var bad = await _client.PostAsJsonAsync("/api/v1/auths/update/password",
            new UpdatePasswordRequest("errada", "novasenha1"));
        Assert.That(bad.StatusCode, Is.Not.EqualTo(HttpStatusCode.OK));

        Assert.That((await _client.PostAsJsonAsync("/api/v1/auths/update/password",
            new UpdatePasswordRequest("senha123", "novasenha1"))).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));

        _client.DefaultRequestHeaders.Authorization = null;
        var signin = await _client.PostAsJsonAsync("/api/v1/auths/signin",
            new SignInRequest("prof@ecov.local", "novasenha1"));
        Assert.That(signin.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Admin cria usuário e e-mail duplicado falha.
        UseToken(_adminToken);
        Assert.That((await _client.PostAsJsonAsync("/api/v1/auths/add",
            new AddUserRequest("Criado", "criado@ecov.local", "senha123", "user"))).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PostAsJsonAsync("/api/v1/auths/add",
            new AddUserRequest("Dup", "criado@ecov.local", "senha123", "user"))).StatusCode,
            Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---------------- Knowledge ----------------

    [Test]
    public async Task Knowledge_Crud_Access_BatchDelete_NotFound()
    {
        var auth = await SignUpAsync("Kn", "kn@ecov.local", "senha123");
        UseToken(auth.Token);

        var created = await _client.PostAsJsonAsync("/api/v1/knowledge/",
            new CreateKnowledgeRequest("Base", "desc"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var kb = await created.Content.ReadFromJsonAsync<KnowledgeResponse>();
        var id = kb!.Id;

        Assert.That((await _client.GetAsync("/api/v1/knowledge/")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.GetAsync($"/api/v1/knowledge/{id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PutAsJsonAsync($"/api/v1/knowledge/{id}",
            new UpdateKnowledgeRequest("Base2", null))).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PostAsync($"/api/v1/knowledge/{id}/reindex", null)).StatusCode,
            Is.Not.EqualTo(HttpStatusCode.NotFound));

        Assert.That((await _client.GetAsync($"/api/v1/knowledge/{id}/access")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PostAsJsonAsync($"/api/v1/knowledge/{id}/access/update",
            new AccessUpdateRequest([new AccessGrant("user", auth.User.Id, "read")]))).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));

        // Inexistente → 404 em get/update/delete/access.
        Assert.That((await _client.GetAsync("/api/v1/knowledge/zzz")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.PutAsJsonAsync("/api/v1/knowledge/zzz",
            new UpdateKnowledgeRequest("x", null))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // Batch delete numa segunda coleção; delete individual na primeira.
        var created2 = await _client.PostAsJsonAsync("/api/v1/knowledge/",
            new CreateKnowledgeRequest("Batch", null));
        var kb2 = await created2.Content.ReadFromJsonAsync<KnowledgeResponse>();
        Assert.That((await _client.PostAsJsonAsync("/api/v1/knowledge/batch/delete",
            new BatchKnowledgeRequest([kb2!.Id]))).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.DeleteAsync($"/api/v1/knowledge/{id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
    }

    // ---------------- Skills / Functions / Pipelines ----------------

    [Test]
    public async Task Skills_Crud_NotFound()
    {
        UseToken(_adminToken);

        var created = await _client.PostAsJsonAsync("/api/v1/skills/",
            new SkillRequest("skill-a", "desc", "conteúdo"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var skill = await created.Content.ReadFromJsonAsync<JsonElementShim.IdDoc>();

        Assert.That((await _client.GetAsync("/api/v1/skills/")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.GetAsync($"/api/v1/skills/{skill!.Id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PostAsJsonAsync($"/api/v1/skills/{skill.Id}",
            new SkillRequest("skill-b", null, "novo"))).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        Assert.That((await _client.GetAsync("/api/v1/skills/zzz")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.PostAsJsonAsync("/api/v1/skills/zzz",
            new SkillRequest("x", null, "y"))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.DeleteAsync("/api/v1/skills/zzz")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.DeleteAsync($"/api/v1/skills/{skill.Id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Functions_Crud_Toggle_Valves_NotFound()
    {
        UseToken(_adminToken);

        var created = await _client.PostAsJsonAsync("/api/v1/functions/",
            new FunctionRequest("fn-a", "filter", null, null));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var fn = await created.Content.ReadFromJsonAsync<JsonElementShim.IdDoc>();

        Assert.That((await _client.GetAsync("/api/v1/functions/")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.GetAsync($"/api/v1/functions/{fn!.Id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PostAsJsonAsync($"/api/v1/functions/{fn.Id}",
            new FunctionRequest("fn-b", "filter", "{}", null))).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PostAsync($"/api/v1/functions/{fn.Id}/toggle", null)).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PostAsJsonAsync($"/api/v1/functions/{fn.Id}/valves",
            new FunctionValvesRequest("{\"a\":1}"))).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        Assert.That((await _client.GetAsync("/api/v1/functions/zzz")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.PostAsJsonAsync("/api/v1/functions/zzz/valves",
            new FunctionValvesRequest("{}"))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.DeleteAsync($"/api/v1/functions/{fn.Id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Pipelines_ServerCrud_List()
    {
        UseToken(_adminToken);

        Assert.That((await _client.GetAsync("/api/v1/pipelines/")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.GetAsync("/api/v1/pipelines/list")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));

        var created = await _client.PostAsJsonAsync("/api/v1/pipelines/",
            new PipelineServerRequest("srv", "http://pipe.local", "key1"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var server = await created.Content.ReadFromJsonAsync<JsonElementShim.IdDoc>();
        Assert.That(server!.Id, Is.Not.Empty);
        Assert.That((await _client.DeleteAsync($"/api/v1/pipelines/{server.Id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
    }

    // ---------------- Models ----------------

    [Test]
    public async Task Models_Create_Update_Access_Toggle_Export_Import()
    {
        UseToken(_adminToken);

        Assert.That((await _client.GetAsync("/api/v1/models/list")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));

        var created = await _client.PostAsJsonAsync("/api/v1/models/create",
            new ModelEntryUpsertRequest("meu-modelo", "base-x", "sys", "{}", null));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var model = await created.Content.ReadFromJsonAsync<JsonElementShim.IdDoc>();
        var id = model!.Id;

        Assert.That((await _client.PostAsJsonAsync("/api/v1/models/model/update",
            new ModelEndpoints.ModelUpdateRequest(id, "nome2", null, null, null, null))).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.GetAsync($"/api/v1/models/model/access?id={id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PostAsJsonAsync($"/api/v1/models/model/access/update?id={id}",
            new AccessUpdateRequest([new AccessGrant("user", "qualquer-id", "read")]))).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PostAsJsonAsync($"/api/v1/models/model/access/update?id={id}",
            new AccessUpdateRequest([new AccessGrant("bogus", "x", "admin")]))).StatusCode,
            Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await _client.GetAsync("/api/v1/models/model/access?id=zzz")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));

        Assert.That((await _client.GetAsync("/api/v1/models/export")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PostAsJsonAsync("/api/v1/models/import",
            new[] { new ModelEntryUpsertRequest("importado", null, null, null, null) })).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));

        Assert.That((await _client.PostAsJsonAsync("/api/v1/models/model/toggle",
            new ModelEndpoints.ToggleRequest(id))).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PostAsJsonAsync("/api/v1/models/model/delete",
            new ModelEndpoints.ToggleRequest(id))).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    // ---------------- Users ----------------

    [Test]
    public async Task Users_Search_Settings_Permissions_Update()
    {
        var auth = await SignUpAsync("Usr", "usr@ecov.local", "senha123");
        UseToken(auth.Token);

        // active é admin-only → Forbidden para user comum; search é liberada.
        Assert.That((await _client.GetAsync("/api/v1/users/search?q=usr")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.GetAsync("/api/v1/users/active")).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden));

        Assert.That((await _client.GetAsync("/api/v1/users/user/settings")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PostAsJsonAsync("/api/v1/users/user/settings",
            new UserSettingsRequest("{\"theme\":\"dark\"}"))).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));

        // Admin: search/active + get/update do usuário + permissions.
        UseToken(_adminToken);
        Assert.That((await _client.GetAsync("/api/v1/users/search?q=usr")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.GetAsync("/api/v1/users/active")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.GetAsync("/api/v1/users/permissions")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.GetAsync($"/api/v1/users/{auth.User.Id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.GetAsync($"/api/v1/users/{auth.User.Id}/permissions")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PostAsJsonAsync($"/api/v1/users/{auth.User.Id}/update",
            new AdminUpdateUserRequest("Usr2", null, null, null))).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.GetAsync("/api/v1/users/zzz")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---------------- Retrieval ----------------

    [Test]
    public async Task Retrieval_Config_Reset_ProcessGuards()
    {
        var auth = await SignUpAsync("Rt", "rt@ecov.local", "senha123");
        UseToken(auth.Token);

        // Config é admin-only → Forbidden para user comum.
        Assert.That((await _client.GetAsync("/api/v1/retrieval/config")).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden));

        UseToken(_adminToken);
        var cfg = await _client.GetAsync("/api/v1/retrieval/config");
        Assert.That(cfg.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var current = (await cfg.Content.ReadFromJsonAsync<RetrievalConfig>())!;

        Assert.That((await _client.PostAsJsonAsync("/api/v1/retrieval/config/update",
            current with { TopK = 7 })).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        Assert.That((await _client.PostAsync("/api/v1/retrieval/reset/db", null)).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PostAsync("/api/v1/retrieval/reset/uploads", null)).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PostAsync("/api/v1/retrieval/reset/bogus", null)).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));

        UseToken(auth.Token);
        // process/text funciona sem provider de embedding (fica pendente, mas 200).
        var text = await _client.PostAsJsonAsync("/api/v1/retrieval/process/text",
            new ProcessTextRequest("conteúdo de teste", "doc", null));
        Assert.That((int)text.StatusCode, Is.LessThan(500));

        // URL inválida → erro de loader propagado (502/400, não 500 não tratado).
        var url = await _client.PostAsJsonAsync("/api/v1/retrieval/process/url",
            new ProcessUrlRequest("ftp://invalido", null));
        Assert.That((int)url.StatusCode, Is.Not.EqualTo(200));
        Assert.That((int)url.StatusCode, Is.LessThan(500).Or.EqualTo(502));

        var yt = await _client.PostAsJsonAsync("/api/v1/retrieval/process/youtube",
            new ProcessUrlRequest("https://ex.com/nao-e-youtube", null));
        Assert.That((int)yt.StatusCode, Is.LessThan(500).Or.EqualTo(502));

        var ws = await _client.PostAsJsonAsync("/api/v1/retrieval/process/web/search",
            new { query = "devin" });
        // Sem engine → 503; engine ok → 200; engine inalcançável → 502. Nunca 404/500 solto.
        Assert.That((int)ws.StatusCode, Is.EqualTo(200).Or.EqualTo(503).Or.EqualTo(502));
    }

    // ---------------- helpers ----------------

    /// <summary>DTO mínimo para ler o id de respostas de criação.</summary>
    private static class JsonElementShim
    {
        public sealed record IdDoc(string Id);
    }
}
