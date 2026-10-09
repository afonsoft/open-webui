using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura da Workspace File API (SPEC-20261009-workspace-file-api /
/// E16 S1): endpoints <c>/api/v1/workspace/repo/{tree,file,mkdir,rename,
/// delete}</c> sobre o workdir do repo vinculado — jail ResolveInside,
/// symlink, paginação da tree, etag/If-Match, binary-guard, 404 sem
/// binding e trilha de auditoria nas escritas.
/// </summary>
[TestFixture]
public class WorkspaceFileEndpointsTests
{
    private string _apiDir = null!;

    [SetUp]
    public void SetUp() => _apiDir = FindApiDir();

    // ---------- RF-004 + auth ----------

    [Test]
    public async Task Endpoints_SemAuth_401()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        foreach (var uri in new[]
        {
            "/api/v1/workspace/repo/tree?path=",
            "/api/v1/workspace/repo/file?path=a.txt",
        })
        {
            var response = await client.GetAsync(uri);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), uri);
        }
    }

    [Test]
    public async Task Endpoints_SemRepoVinculado_404BoundFalse()
    {
        using var ctx = await NewAppAsync(bound: false);
        foreach (var uri in new[]
        {
            "/api/v1/workspace/repo/tree",
            "/api/v1/workspace/repo/file?path=a.txt",
        })
        {
            var get = await ctx.Client.GetAsync(uri);
            Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), uri);
            var body = await get.Content.ReadFromJsonAsync<JsonElement>();
            Assert.That(body.GetProperty("bound").GetBoolean(), Is.False, uri);
        }

        var put = await ctx.Client.PutAsJsonAsync("/api/v1/workspace/repo/file",
            new WorkspaceFileWriteRequest("a.txt", "x"));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        foreach (var (uri, body) in new (string, object)[]
        {
            ("/api/v1/workspace/repo/mkdir", new WorkspaceFileMkdirRequest("d")),
            ("/api/v1/workspace/repo/rename", new WorkspaceFileRenameRequest("a", "b")),
            ("/api/v1/workspace/repo/delete", new WorkspaceFileDeleteRequest("a")),
        })
        {
            var post = await ctx.Client.PostAsJsonAsync(uri, body);
            Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), uri);
        }
    }

    // ---------- RF-001: tree ----------

    [Test]
    public async Task Tree_Raiz_DirsPrimeiroEDirsComunsColapsados()
    {
        using var ctx = await NewAppAsync(bound: true);
        var w = ctx.Workdir!;
        Directory.CreateDirectory(Path.Join(w, "src"));
        Directory.CreateDirectory(Path.Join(w, "bin"));
        File.WriteAllText(Path.Join(w, "readme.md"), "oi\n");
        File.WriteAllText(Path.Join(w, "a.cs"), "class A {}\n");

        var tree = await ctx.Client.GetFromJsonAsync<WorkspaceFileTreeResponse>(
            "/api/v1/workspace/repo/tree?path=&depth=1");
        Assert.That(tree, Is.Not.Null);
        var names = tree!.Entries.Select(e => e.Name).ToList();
        Assert.Multiple(() =>
        {
            // dirs primeiro, ordem de nome; .git da fixture é colapsado
            Assert.That(names[0], Is.EqualTo(".git"));
            Assert.That(names, Does.Contain("src").And.Contain("bin")
                .And.Contain("a.cs").And.Contain("readme.md"));
            Assert.That(tree.Entries.First(e => e.Name == ".git").Collapsed, Is.True);
            Assert.That(tree.Entries.First(e => e.Name == "bin").Collapsed, Is.True);
            Assert.That(tree.Entries.First(e => e.Name == "src").Collapsed, Is.False);
            Assert.That(tree.Entries.First(e => e.Name == "a.cs").Type, Is.EqualTo("file"));
            Assert.That(tree.Entries.First(e => e.Name == "a.cs").Size, Is.EqualTo(11));
        });
    }

    [Test]
    public async Task Tree_SubdirEDepth2()
    {
        using var ctx = await NewAppAsync(bound: true);
        var w = ctx.Workdir!;
        Directory.CreateDirectory(Path.Join(w, "src", "inner"));
        File.WriteAllText(Path.Join(w, "src", "inner", "f.cs"), "x");

        var tree = await ctx.Client.GetFromJsonAsync<WorkspaceFileTreeResponse>(
            "/api/v1/workspace/repo/tree?path=src&depth=1");
        Assert.That(tree!.Entries.Select(e => e.Path), Does.Contain("src/inner"));

        // src/inner tem profundidade 2; o arquivo dentro dele, profundidade 3.
        var two = await ctx.Client.GetFromJsonAsync<WorkspaceFileTreeResponse>(
            "/api/v1/workspace/repo/tree?path=&depth=2");
        Assert.That(two!.Entries.Select(e => e.Path),
            Does.Contain("src/inner").And.Not.Contain("src/inner/f.cs"));
        var deep = await ctx.Client.GetFromJsonAsync<WorkspaceFileTreeResponse>(
            "/api/v1/workspace/repo/tree?path=&depth=3");
        Assert.That(deep!.Entries.Select(e => e.Path), Does.Contain("src/inner/f.cs"));
    }

    [Test]
    public async Task Tree_EscapePaths_400()
    {
        using var ctx = await NewAppAsync(bound: true);
        foreach (var p in new[] { "..", "../x", "/etc", "~/x", "$HOME/x" })
        {
            var response = await ctx.Client.GetAsync(
                $"/api/v1/workspace/repo/tree?path={Uri.EscapeDataString(p)}");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), p);
        }
    }

    [Test]
    public async Task Tree_SymlinkParaFora_Rejeitado()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Ignore("symlink sem privilege no Windows CI.");
        }
        using var ctx = await NewAppAsync(bound: true);
        var outside = Path.Join(Path.GetTempPath(), $"owui-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Join(ctx.Workdir!, "link-out"), outside);
            var response = await ctx.Client.GetAsync("/api/v1/workspace/repo/tree?path=link-out");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Test]
    public async Task Tree_SymlinkDir_NaoListaFilhosForaDoJail()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Ignore("symlink sem privilege no Windows CI.");
        }
        using var ctx = await NewAppAsync(bound: true);
        var outside = Path.Join(Path.GetTempPath(), $"owui-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Join(outside, "segredo.txt"), "não olhar");
        try
        {
            Directory.CreateSymbolicLink(Path.Join(ctx.Workdir!, "link-out"), outside);
            var tree = await ctx.Client.GetFromJsonAsync<WorkspaceFileTreeResponse>(
                "/api/v1/workspace/repo/tree?path=&depth=3");
            Assert.That(tree, Is.Not.Null);
            Assert.Multiple(() =>
            {
                // o symlink aparece como dir colapsado...
                Assert.That(tree!.Entries.First(e => e.Name == "link-out").Collapsed, Is.True);
                // ...e nenhum filho do alvo fora do jail é listado
                Assert.That(tree.Entries.Any(e => e.Name == "segredo.txt"), Is.False);
            });
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    // ---------- RF-002: read ----------

    [Test]
    public async Task Read_PaginaLinhas_RetornaFatiaETag()
    {
        using var ctx = await NewAppAsync(bound: true);
        File.WriteAllText(Path.Join(ctx.Workdir!, "f.txt"),
            string.Join("\n", Enumerable.Range(1, 10).Select(i => $"l{i}")) + "\n");

        var response = await ctx.Client.GetAsync(
            "/api/v1/workspace/repo/file?path=f.txt&startLine=3&maxLines=4");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.ETag, Is.Not.Null);

        var body = await response.Content.ReadFromJsonAsync<WorkspaceFileReadResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(body!.Path, Is.EqualTo("f.txt"));
            Assert.That(body.Content, Is.EqualTo("l3\nl4\nl5\nl6"));
            Assert.That(body.TotalLines, Is.EqualTo(10));
            Assert.That(body.Truncated, Is.True);
            Assert.That(body.ETag, Does.StartWith("\""));
        });
    }

    [Test]
    public async Task Read_Binario_415_E_Grande_413()
    {
        using var ctx = await NewAppAsync(bound: true);
        File.WriteAllBytes(Path.Join(ctx.Workdir!, "bin.dat"), new byte[] { 0, 1, 2, 3 });

        var bin = await ctx.Client.GetAsync("/api/v1/workspace/repo/file?path=bin.dat");
        Assert.That(bin.StatusCode, Is.EqualTo(HttpStatusCode.UnsupportedMediaType));

        var big = Path.Join(ctx.Workdir!, "big.txt");
        File.WriteAllText(big, new string('a', 600 * 1024));
        var bigResponse = await ctx.Client.GetAsync("/api/v1/workspace/repo/file?path=big.txt");
        Assert.That(bigResponse.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        var body = await bigResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("size").GetInt64(), Is.EqualTo(600 * 1024));
    }

    [Test]
    public async Task Read_NaoExiste_404_E_Escape_400()
    {
        using var ctx = await NewAppAsync(bound: true);
        var nf = await ctx.Client.GetAsync("/api/v1/workspace/repo/file?path=nada.txt");
        Assert.That(nf.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var esc = await ctx.Client.GetAsync(
            "/api/v1/workspace/repo/file?path=..%2FProgram.cs");
        Assert.That(esc.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---------- RF-003: write ops ----------

    [Test]
    public async Task Write_CriaArquivo_ERespondeETag()
    {
        using var ctx = await NewAppAsync(bound: true);
        var put = await ctx.Client.PutAsJsonAsync("/api/v1/workspace/repo/file",
            new WorkspaceFileWriteRequest("novo/dir/f.txt", "conteúdo\n"));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await put.Content.ReadFromJsonAsync<WorkspaceFileWriteResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(body!.Ok, Is.True);
            Assert.That(body.Path, Is.EqualTo("novo/dir/f.txt"));
            Assert.That(body.ETag, Does.StartWith("\""));
        });
        Assert.That(File.ReadAllText(Path.Join(ctx.Workdir!, "novo/dir/f.txt")),
            Is.EqualTo("conteúdo\n"));
    }

    [Test]
    public async Task Write_IfMatchDivergente_409()
    {
        using var ctx = await NewAppAsync(bound: true);
        File.WriteAllText(Path.Join(ctx.Workdir!, "f.txt"), "v1\n");

        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/workspace/repo/file")
        {
            Content = JsonContent.Create(new WorkspaceFileWriteRequest("f.txt", "v2\n")),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"0-0\"");
        var response = await ctx.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        // etag atual vem no conflito para a UI oferecer merge/descartar
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("etag").GetString(), Does.StartWith("\""));
        Assert.That(File.ReadAllText(Path.Join(ctx.Workdir!, "f.txt")), Is.EqualTo("v1\n"));
    }

    [Test]
    public async Task Write_IfMatchCorreto_Sobrescreve()
    {
        using var ctx = await NewAppAsync(bound: true);
        File.WriteAllText(Path.Join(ctx.Workdir!, "f.txt"), "v1\n");
        var read = await ctx.Client.GetFromJsonAsync<WorkspaceFileReadResponse>(
            "/api/v1/workspace/repo/file?path=f.txt");

        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/workspace/repo/file")
        {
            Content = JsonContent.Create(new WorkspaceFileWriteRequest("f.txt", "v2\n")),
        };
        request.Headers.TryAddWithoutValidation("If-Match", read!.ETag);
        var response = await ctx.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(File.ReadAllText(Path.Join(ctx.Workdir!, "f.txt")), Is.EqualTo("v2\n"));
    }

    [Test]
    public async Task Write_ConteudoBinario_415()
    {
        using var ctx = await NewAppAsync(bound: true);
        var put = await ctx.Client.PutAsJsonAsync("/api/v1/workspace/repo/file",
            new WorkspaceFileWriteRequest("b.bin", "a b"));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.UnsupportedMediaType));
    }

    [Test]
    public async Task Mkdir_Rename_Delete_Fluxo()
    {
        using var ctx = await NewAppAsync(bound: true);
        var w = ctx.Workdir!;
        File.WriteAllText(Path.Join(w, "orig.txt"), "x\n");

        var mk = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/mkdir",
            new WorkspaceFileMkdirRequest("novo/sub"));
        Assert.That(mk.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(Directory.Exists(Path.Join(w, "novo/sub")), Is.True);

        var rn = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/rename",
            new WorkspaceFileRenameRequest("orig.txt", "novo/movido.txt"));
        Assert.That(rn.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Join(w, "novo/movido.txt")), Is.True);
            Assert.That(File.Exists(Path.Join(w, "orig.txt")), Is.False);
        });

        // rename para destino existente → 409
        File.WriteAllText(Path.Join(w, "existe.txt"), "y\n");
        var rn2 = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/rename",
            new WorkspaceFileRenameRequest("novo/movido.txt", "existe.txt"));
        Assert.That(rn2.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        var del = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/delete",
            new WorkspaceFileDeleteRequest("novo"));
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(Directory.Exists(Path.Join(w, "novo")), Is.False);
    }

    [Test]
    public async Task Rename_ParaDentroDeSi_409()
    {
        using var ctx = await NewAppAsync(bound: true);
        Directory.CreateDirectory(Path.Join(ctx.Workdir!, "pai"));
        var rn = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/rename",
            new WorkspaceFileRenameRequest("pai", "pai/filho"));
        Assert.That(rn.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(Directory.Exists(Path.Join(ctx.Workdir!, "pai")), Is.True);
    }

    [Test]
    public async Task Write_PathEscape_400()
    {
        using var ctx = await NewAppAsync(bound: true);
        var put = await ctx.Client.PutAsJsonAsync("/api/v1/workspace/repo/file",
            new WorkspaceFileWriteRequest("../fora.txt", "x"));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var rn = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/rename",
            new WorkspaceFileRenameRequest("a", "../b"));
        Assert.That(rn.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---------- RF-005: auditoria ----------

    [Test]
    public async Task Write_RegistraAuditoria()
    {
        var spy = new SpyLoggerProvider();
        var dbPath = Path.Join(Path.GetTempPath(), $"openwebui-wf-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={dbPath}");
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(spy)));
        var (client, workdir, uid) = await SignUpAndBindAsync(factory, dbPath);
        try
        {
            var put = await client.PutAsJsonAsync("/api/v1/workspace/repo/file",
                new WorkspaceFileWriteRequest("audit.txt", "x\n"));
            Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(
                spy.Records.Any(r => r.Contains("audit.txt") && r.Contains(uid)),
                Is.True, $"auditoria ausente; registros: {string.Join(" | ", spy.Records)}");
        }
        finally
        {
            CleanupBoundWorkspace(uid);
        }
    }

    // ---------- fixture helpers ----------

    private sealed class Ctx : IDisposable
    {
        private readonly WebApplicationFactory<Program> _factory;

        public Ctx(WebApplicationFactory<Program> factory, HttpClient client,
            string? workdir, string userId)
        {
            _factory = factory;
            Client = client;
            Workdir = workdir;
            UserId = userId;
        }

        public HttpClient Client { get; }
        public string? Workdir { get; }
        public string UserId { get; }

        public void Dispose()
        {
            Client.Dispose();
            _factory.Dispose();
            CleanupBoundWorkspace(UserId);
        }
    }

    /// <summary>App real + usuário; quando <paramref name="bound"/>, semeia binding + workdir.</summary>
    private async Task<Ctx> NewAppAsync(bool bound)
    {
        var dbPath = Path.Join(Path.GetTempPath(), $"openwebui-wf-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={dbPath}");
        var factory = new WebApplicationFactory<Program>();
        var (client, workdir, uid) = await SignUpAndBindAsync(factory, dbPath, bound);
        return new Ctx(factory, client, workdir, uid);
    }

    private async Task<(HttpClient Client, string? Workdir, string UserId)> SignUpAndBindAsync(
        WebApplicationFactory<Program> factory, string dbPath, bool bound = true)
    {
        var client = factory.CreateClient();
        var tag = Guid.NewGuid().ToString("N")[..6];
        var signup = await client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest($"WF{tag}", $"wf{tag}@wf.local", "senha123"));
        Assert.That(signup.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await signup.Content.ReadAsStringAsync());
        var auth = (await signup.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.Token);

        var uid = auth.User.Id;
        string? workdir = null;
        if (bound)
        {
            const string dir = "repos/test__repo";
            workdir = Path.Join(_apiDir, "data", "workspaces", uid, dir);
            Directory.CreateDirectory(Path.Join(workdir, ".git"));
            await SeedBindingAsync(dbPath, uid, dir);
        }
        return (client, workdir, uid);
    }

    /// <summary>Semeia o kv <c>u:{uid}:workspace.repo</c> direto no SQLite da app.</summary>
    private static async Task SeedBindingAsync(string dbPath, string uid, string dir)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options);
        var json = JsonSerializer.Serialize(
            new WorkspaceRepoBinding("test/repo", "main", dir));
        db.ConfigEntries.Add(new ConfigEntry { Key = $"u:{uid}:workspace.repo", ValueJson = json });
        await db.SaveChangesAsync();
    }

    /// <summary>Remove o workdir semeado sob o content root da API (gitignored).</summary>
    private static void CleanupBoundWorkspace(string uid)
    {
        try
        {
            var dir = Path.Join(FindApiDir(), "data", "workspaces", uid);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>Resolve <c>src/OpenWebUI.Api</c> subindo a partir do bin de testes.</summary>
    private static string FindApiDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Join(dir.FullName, "src", "OpenWebUI.Api")))
        {
            dir = dir.Parent;
        }
        Assert.That(dir, Is.Not.Null, "repo root não encontrado a partir do bin de testes");
        return Path.Join(dir!.FullName, "src", "OpenWebUI.Api");
    }

    /// <summary>Captura Log* renderizados para o assert de auditoria (RF-005).</summary>
    private sealed class SpyLoggerProvider : ILoggerProvider
    {
        public List<string> Records { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Spy(Records);

        public void Dispose() { }

        private sealed class Spy(List<string> records) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (records)
                {
                    records.Add(formatter(state, exception));
                }
            }
        }
    }
}
