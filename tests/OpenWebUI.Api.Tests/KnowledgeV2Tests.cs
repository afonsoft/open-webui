using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes da slice knowledge-v2: anexar file_id existente, reindexar
/// coleção com o provider atual e exclusão em lote respeitando dono.
/// </summary>
[TestFixture]
[NonParallelizable]
public class KnowledgeV2Tests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;
    private AuthResponse _user = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private string _mockBase = null!;
    private bool _embedOk = true;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-kgv2-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = (await (await _client.PostAsJsonAsync(
                "/api/v1/auths/signup", new SignUpRequest("Admin", "admin@kg2.local", "senha123")))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        UseToken(_admin.Token);
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });

        _user = (await (await _client.PostAsJsonAsync(
                "/api/v1/auths/signup", new SignUpRequest("User", "user@kg2.local", "senha123")))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        _mockBase = StartMock();
        UseToken(_admin.Token);
        await _client.PostAsJsonAsync("/api/v1/configs/connections",
            new ConnectionsConfig([_mockBase], [], []));
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _mockCts.Cancel();
        _mock.Stop();
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private string StartMock()
    {
        var port = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            port = new Random().Next(40000, 60000);
            _mock = new HttpListener();
            _mock.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                _mock.Start();
                break;
            }
            catch (HttpListenerException)
            {
                _mock.Close();
            }
        }
        if (!_mock.IsListening)
        {
            throw new InvalidOperationException("Nenhuma porta livre para o mock.");
        }
        _mockCts = new CancellationTokenSource();
        _ = Task.Run(() => MockLoopAsync(_mockCts.Token));
        return $"http://localhost:{port}";
    }

    private async Task MockLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _mock.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return;
            }

            try
            {
                if (ctx.Request.Url!.AbsolutePath == "/api/embed" && _embedOk)
                {
                    var bytes = Encoding.UTF8.GetBytes("{\"embeddings\":[[1,0,0]]}");
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes, ct);
                }
                else
                {
                    ctx.Response.StatusCode = 502;
                }
            }
            finally
            {
                ctx.Response.Close();
            }
        }
    }

    private async Task<string> UploadTextFileAsync(string name, string content)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", name);
        var response = await _client.PostAsync("/api/v1/files/", form);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private async Task<KnowledgeResponse> CreateCollectionAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/knowledge",
            new CreateKnowledgeRequest(name, null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<KnowledgeResponse>())!;
    }

    [Test, Order(1)]
    public async Task FileAdd_VinculaArquivoEnviadoEIndexa()
    {
        _embedOk = true;
        UseToken(_admin.Token);
        var fileId = await UploadTextFileAsync("doc.txt", "conteúdo indexável do documento");
        var collection = await CreateCollectionAsync("col-file-add");

        var added = await _client.PostAsJsonAsync(
            $"/api/v1/knowledge/{collection.Id}/file/add", new AddKnowledgeFileRequest(fileId));
        Assert.That(added.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var body = JsonDocument.Parse(await added.Content.ReadAsStringAsync());
        Assert.That(body.RootElement.GetProperty("indexed").GetBoolean(), Is.True);

        var detail = await _client.GetFromJsonAsync<KnowledgeDetailResponse>(
            $"/api/v1/knowledge/{collection.Id}");
        Assert.That(detail!.Files.Select(f => f.FileId), Does.Contain(fileId));
    }

    [Test, Order(2)]
    public async Task FileAdd_ArquivoInexistente_404()
    {
        UseToken(_admin.Token);
        var collection = await CreateCollectionAsync("col-404");
        var added = await _client.PostAsJsonAsync(
            $"/api/v1/knowledge/{collection.Id}/file/add",
            new AddKnowledgeFileRequest("nao-existe"));
        Assert.That(added.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test, Order(3)]
    public async Task FileAdd_ArquivoDeOutroUsuario_404()
    {
        UseToken(_user.Token);
        var fileId = await UploadTextFileAsync("do-user.txt", "texto do usuário");
        UseToken(_admin.Token);
        var collection = await CreateCollectionAsync("col-outro-user");
        var added = await _client.PostAsJsonAsync(
            $"/api/v1/knowledge/{collection.Id}/file/add", new AddKnowledgeFileRequest(fileId));
        Assert.That(added.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test, Order(4)]
    public async Task Reindex_SucessoComProvider()
    {
        _embedOk = true;
        UseToken(_admin.Token);
        var fileId = await UploadTextFileAsync("reidx.txt", "texto para reindexar");
        var collection = await CreateCollectionAsync("col-reindex");
        await _client.PostAsJsonAsync(
            $"/api/v1/knowledge/{collection.Id}/file/add", new AddKnowledgeFileRequest(fileId));

        var reindex = await _client.PostAsync($"/api/v1/knowledge/{collection.Id}/reindex", null);
        Assert.That(reindex.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var body = JsonDocument.Parse(await reindex.Content.ReadAsStringAsync());
        Assert.Multiple(() =>
        {
            Assert.That(body.RootElement.GetProperty("indexed").GetInt32(), Is.EqualTo(1));
            Assert.That(body.RootElement.GetProperty("failed").GetInt32(), Is.EqualTo(0));
        });
    }

    [Test, Order(5)]
    public async Task Reindex_FalhaDeProvider_502ComContagem()
    {
        UseToken(_admin.Token);
        var fileId = await UploadTextFileAsync("reidx-fail.txt", "texto que falha");
        var collection = await CreateCollectionAsync("col-reindex-fail");
        await _client.PostAsJsonAsync(
            $"/api/v1/knowledge/{collection.Id}/file/add", new AddKnowledgeFileRequest(fileId));

        _embedOk = false;
        var reindex = await _client.PostAsync($"/api/v1/knowledge/{collection.Id}/reindex", null);
        _embedOk = true;
        Assert.That(reindex.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
        using var body = JsonDocument.Parse(await reindex.Content.ReadAsStringAsync());
        Assert.That(body.RootElement.GetProperty("failed").GetInt32(), Is.EqualTo(1));
    }

    [Test, Order(6)]
    public async Task BatchDelete_RespeitaDonoEAdmin()
    {
        UseToken(_admin.Token);
        var c1 = await CreateCollectionAsync("batch-a1");
        var c2 = await CreateCollectionAsync("batch-a2");
        UseToken(_user.Token);
        var c3 = await CreateCollectionAsync("batch-u1");

        // Usuário comum não deleta coleções alheias.
        var denied = await _client.PostAsJsonAsync("/api/v1/knowledge/batch/delete",
            new BatchKnowledgeRequest([c1.Id, c3.Id]));
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var deniedBody = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Assert.That(deniedBody.RootElement.GetProperty("deleted").GetInt32(), Is.EqualTo(1),
            "só a coleção própria deve ser removida");

        // Admin deleta todas.
        UseToken(_admin.Token);
        var deleted = await _client.PostAsJsonAsync("/api/v1/knowledge/batch/delete",
            new BatchKnowledgeRequest([c1.Id, c2.Id]));
        using var body = JsonDocument.Parse(await deleted.Content.ReadAsStringAsync());
        Assert.That(body.RootElement.GetProperty("deleted").GetInt32(), Is.EqualTo(2));

        UseToken(_user.Token);
        var gone = await _client.GetAsync($"/api/v1/knowledge/{c1.Id}");
        Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test, Order(7)]
    public async Task BatchDelete_SemAutenticacao_401()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var response = await _client.PostAsJsonAsync("/api/v1/knowledge/batch/delete",
            new BatchKnowledgeRequest(["x"]));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test, Order(8)]
    public async Task List_FileCount_RefleteArquivosVinculados()
    {
        _embedOk = true;
        UseToken(_admin.Token);
        var fileId = await UploadTextFileAsync("fc.txt", "conteúdo para contar");
        var collection = await CreateCollectionAsync("col-filecount");

        var list0 = await _client.GetFromJsonAsync<List<KnowledgeResponse>>("/api/v1/knowledge");
        Assert.That(list0!.First(c => c.Id == collection.Id).FileCount, Is.EqualTo(0));

        var added = await _client.PostAsJsonAsync(
            $"/api/v1/knowledge/{collection.Id}/file/add", new AddKnowledgeFileRequest(fileId));
        Assert.That(added.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var list1 = await _client.GetFromJsonAsync<List<KnowledgeResponse>>("/api/v1/knowledge");
        Assert.That(list1!.First(c => c.Id == collection.Id).FileCount, Is.EqualTo(1));
    }
}
