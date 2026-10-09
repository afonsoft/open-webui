using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes da slice rag-knowledge: coleções, chunks e retrieval.</summary>
[TestFixture, IsolateEnvironment]
public class KnowledgeEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;
    private AuthResponse _user = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-kg-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _admin = await SignUpAsync("Admin", "admin@test.local", "senha123");
        UseToken(_admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
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

    [Test]
    public async Task Knowledge_CRUD_Duplicado409() // RF-002
    {
        UseToken(_admin.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/knowledge",
            new CreateKnowledgeRequest("docs", "Documentos do produto"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var collection = (await created.Content.ReadFromJsonAsync<KnowledgeResponse>())!;

        var duplicate = await _client.PostAsJsonAsync("/api/v1/knowledge",
            new CreateKnowledgeRequest("docs", null));
        Assert.That(duplicate.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        var list = await _client.GetFromJsonAsync<List<KnowledgeResponse>>("/api/v1/knowledge");
        Assert.That(list!.Any(k => k.Id == collection.Id), Is.True);

        var updated = await _client.PutAsJsonAsync($"/api/v1/knowledge/{collection.Id}",
            new UpdateKnowledgeRequest(null, "Nova descrição"));
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var detail = await _client.GetFromJsonAsync<KnowledgeDetailResponse>(
            $"/api/v1/knowledge/{collection.Id}");
        Assert.That(detail!.Description, Is.EqualTo("Nova descrição"));

        var deleted = await _client.DeleteAsync($"/api/v1/knowledge/{collection.Id}");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Knowledge_AddFile_VinculaEIndexaBestEffort() // RF-001
    {
        UseToken(_admin.Token);
        var fileId = await UploadTextFileAsync("manual.txt", "conteúdo do manual de uso");
        var created = await _client.PostAsJsonAsync("/api/v1/knowledge",
            new CreateKnowledgeRequest("manuais", null));
        var collection = (await created.Content.ReadFromJsonAsync<KnowledgeResponse>())!;

        var added = await _client.PostAsJsonAsync(
            $"/api/v1/knowledge/{collection.Id}/files", new AddKnowledgeFileRequest(fileId));
        Assert.That(added.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var detail = await _client.GetFromJsonAsync<KnowledgeDetailResponse>(
            $"/api/v1/knowledge/{collection.Id}");
        Assert.That(detail!.Files.Select(f => f.FileId), Does.Contain(fileId));

        var removed = await _client.DeleteAsync(
            $"/api/v1/knowledge/{collection.Id}/files/{fileId}");
        Assert.That(removed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Knowledge_NaoDono_404() // RF-002 (isolamento por usuário)
    {
        UseToken(_admin.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/knowledge",
            new CreateKnowledgeRequest("privada", null));
        var collection = (await created.Content.ReadFromJsonAsync<KnowledgeResponse>())!;

        UseToken(_user.Token);
        var get = await _client.GetAsync($"/api/v1/knowledge/{collection.Id}");
        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var list = await _client.GetFromJsonAsync<List<KnowledgeResponse>>("/api/v1/knowledge");
        Assert.That(list!.Any(k => k.Id == collection.Id), Is.False);
    }

    [Test]
    public void Rag_ChunkText_RespeitaTamanhoESobreposicao() // RF-001
    {
        var text = string.Concat(Enumerable.Repeat("abcdefghij", 250)); // 2500 chars
        var chunks = RagService.ChunkText(text, chunkSize: 1000, overlap: 100);
        Assert.That(chunks, Has.Count.GreaterThanOrEqualTo(3));
        Assert.That(chunks.All(c => c.Length <= 1000), Is.True);
        Assert.That(chunks[0][^100..], Is.EqualTo(chunks[1][..100]));
    }

    [Test]
    public async Task Rag_SearchAsync_TopKEIsolamentoPorUsuario() // RF-003
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rag = scope.ServiceProvider.GetRequiredService<RagService>();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        db.EmbeddingChunks.AddRange(
            new EmbeddingChunk { UserId = _admin.User.Id, FileId = "f1", ChunkIndex = 0, Text = "chunk match", EmbeddingJson = "[1,0,0]", CreatedAt = now },
            new EmbeddingChunk { UserId = _admin.User.Id, FileId = "f1", ChunkIndex = 1, Text = "chunk fraco", EmbeddingJson = "[0,1,0]", CreatedAt = now },
            new EmbeddingChunk { UserId = _admin.User.Id, FileId = "f2", ChunkIndex = 0, Text = "outro arquivo", EmbeddingJson = "[1,0,0]", CreatedAt = now },
            new EmbeddingChunk { UserId = _user.User.Id, FileId = "fx", ChunkIndex = 0, Text = "não é meu", EmbeddingJson = "[1,0,0]", CreatedAt = now });
        await db.SaveChangesAsync();

        var result = await rag.SearchAsync(_admin.User.Id, [1, 0, 0], null);
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.Contain("chunk match"));
        Assert.That(result, Does.Contain("outro arquivo"));
        Assert.That(result, Does.Not.Contain("não é meu"));

        var scoped = await rag.SearchAsync(_admin.User.Id, [1, 0, 0], ["f2"]);
        Assert.That(scoped, Does.Contain("outro arquivo"));
        Assert.That(scoped, Does.Not.Contain("chunk match"));
    }

    private async Task<string> UploadTextFileAsync(string name, string content)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", name);
        var response = await _client.PostAsync("/api/v1/files/", form);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email, string password)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new { name, email, password });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
}
