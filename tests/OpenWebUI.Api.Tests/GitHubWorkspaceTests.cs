using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using OpenWebUI.Api.Completions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura da integração GitHub + repo vinculado ao workspace
/// (SPEC-20261008-github-repo-workspace): token por usuário no kv (nunca
/// retornado), listagens via API do GitHub (handler stub), clone/switch de
/// branch do <see cref="WorkspaceRepoService"/> com remote local, e os
/// endpoints <c>/api/v1/github/*</c> + <c>/api/v1/workspace/repo</c>.
/// </summary>
[TestFixture]
public class GitHubWorkspaceTests
{
    private string _root = null!;
    private AppDbContext _db = null!;
    private MemoryCache _cache = null!;
    private ConfigService _config = null!;
    private GitHubStub _github = null!;
    private GitHubService _svc = null!;
    private WorkspaceRepoService _repos = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-gh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Join(_root, "t.db")}").Options);
        DatabaseMigrator.MigrateAsync(_db).GetAwaiter().GetResult();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _config = new ConfigService(_db, _cache);
        _github = new GitHubStub();
        _svc = new GitHubService(new StubFactory(_github), _config);
        _repos = new WorkspaceRepoService(_config, new StubEnv(_root));
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _cache.Dispose();
        _github.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    // ---------------- GitHubService ----------------

    [Test]
    public async Task Status_SemToken_NaoConfigurado()
    {
        var status = await _svc.GetStatusAsync("u1", default);
        Assert.Multiple(() =>
        {
            Assert.That(status.Configured, Is.False);
            Assert.That(status.Login, Is.Null);
        });
        Assert.That(await _svc.ListReposAsync("u1", default), Is.Empty);
    }

    [Test]
    public async Task SetToken_Valido_PersisteLoginENuncaRetornaToken()
    {
        var login = await _svc.SetTokenAsync("u1", "pat-1", default);
        Assert.That(login, Is.EqualTo("afonso"));
        // O handler registra o Bearer enviado à API.
        Assert.That(_github.LastAuth, Is.EqualTo("Bearer pat-1"));

        var status = await _svc.GetStatusAsync("u1", default);
        Assert.Multiple(() =>
        {
            Assert.That(status.Configured, Is.True);
            Assert.That(status.Login, Is.EqualTo("afonso"));
        });
        // O token fica acessível só via GetTokenAsync (uso interno).
        Assert.That(await _svc.GetTokenAsync("u1", default), Is.EqualTo("pat-1"));

        await _svc.ClearTokenAsync("u1", default);
        Assert.That((await _svc.GetStatusAsync("u1", default)).Configured, Is.False);
    }

    [Test]
    public async Task SetToken_Invalido_NaoPersiste()
    {
        _github.Status = HttpStatusCode.Unauthorized;
        Assert.That(await _svc.SetTokenAsync("u1", "bad", default), Is.Null);
        Assert.That(await _svc.GetTokenAsync("u1", default), Is.Null);
    }

    [Test]
    public async Task ListRepos_MapeiaCamposDoGitHub()
    {
        await _svc.SetTokenAsync("u1", "pat-1", default);
        var repos = await _svc.ListReposAsync("u1", default);
        Assert.That(repos, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(repos[0].FullName, Is.EqualTo("afonsoft/open-webui"));
            Assert.That(repos[0].Private, Is.True);
            Assert.That(repos[0].DefaultBranch, Is.EqualTo("main"));
            Assert.That(repos[0].HtmlUrl, Does.Contain("github.com/afonsoft/open-webui"));
            Assert.That(repos[1].Description, Is.EqualTo("repo 2"));
        });
    }

    [Test]
    public async Task ListBranches_RetornaDefaultELista()
    {
        await _svc.SetTokenAsync("u1", "pat-1", default);
        var branches = await _svc.ListBranchesAsync("u1", "afonsoft", "open-webui", default);
        Assert.That(branches, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(branches!.DefaultBranch, Is.EqualTo("main"));
            Assert.That(branches.Branches, Does.Contain("main").And.Contain("dev"));
        });
    }

    // ---------------- WorkspaceRepoService ----------------

    [Test]
    public async Task Repo_Open_CloneLocal_VinculaEResolveWorkdir()
    {
        RequireGit();
        var origin = CriarOrigem("main");
        try
        {
            var (binding, error) = await _repos.OpenAsync(
                "u1", "afonsoft/demo", "main", origin, null, default);
            Assert.That(error, Is.Null);
            Assert.That(binding, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(binding!.Repo, Is.EqualTo("afonsoft/demo"));
                Assert.That(binding.Branch, Is.EqualTo("main"));
                Assert.That(binding.Dir, Is.EqualTo("repos/afonsoft__demo"));
            });

            var workdir = await _repos.ResolveWorkdirAsync("u1", default);
            Assert.That(workdir, Is.EqualTo(
                Path.Join(_root, "data", "workspaces", "u1", "repos", "afonsoft__demo")));
            Assert.That(File.Exists(Path.Join(workdir, "readme.md")), Is.True);

            // O remote gravado não carrega credencial.
            var remote = Git(workdir, "remote", "get-url", "origin");
            Assert.That(remote, Is.Not.Null);
        }
        finally
        {
            Directory.Delete(origin, recursive: true);
        }
    }

    [Test]
    public async Task Repo_Open_SlugOuBranchInvalidos_Erro()
    {
        var (binding, error) = await _repos.OpenAsync(
            "u1", "sem-barra", "main", "unused", null, default);
        Assert.That(binding, Is.Null);
        Assert.That(error, Does.Contain("owner/repo"));

        (binding, error) = await _repos.OpenAsync(
            "u1", "a/b", "branch ruim", "unused", null, default);
        Assert.That(binding, Is.Null);
        Assert.That(error, Does.Contain("Branch"));
    }

    [Test]
    public async Task Repo_Open_TrocaDeBranch_NoCheckoutExistente()
    {
        RequireGit();
        var origin = CriarOrigem("main", extraBranch: "dev");
        try
        {
            var (binding, _) = await _repos.OpenAsync(
                "u1", "a/b", "main", origin, null, default);
            Assert.That(binding?.Branch, Is.EqualTo("main"));

            var dir = Path.Join(_root, "data", "workspaces", "u1", "repos", "a__b");
            File.WriteAllText(Path.Join(dir, "local.txt"), "x\n");

            (binding, var error) = await _repos.OpenAsync(
                "u1", "a/b", "dev", origin, null, default);
            Assert.That(error, Is.Null);
            Assert.That(binding?.Branch, Is.EqualTo("dev"));
            Assert.That(Git(dir, "branch", "--show-current"), Does.Contain("dev"));
            // Arquivo não-rastreado do checkout sobrevive à troca.
            Assert.That(File.Exists(Path.Join(dir, "local.txt")), Is.True);
        }
        finally
        {
            Directory.Delete(origin, recursive: true);
        }
    }

    [Test]
    public async Task Repo_Unbind_VoltaParaRaizDoWorkspace()
    {
        RequireGit();
        var origin = CriarOrigem("main");
        try
        {
            await _repos.OpenAsync("u1", "a/b", "main", origin, null, default);
            await _repos.UnbindAsync("u1", default);
            Assert.That(await _repos.GetBindingAsync("u1", default), Is.Null);
            Assert.That(await _repos.ResolveWorkdirAsync("u1", default),
                Is.EqualTo(Path.Join(_root, "data", "workspaces", "u1")));
        }
        finally
        {
            Directory.Delete(origin, recursive: true);
        }
    }

    // ---------------- EnrichRequestAsync (repo binding) ----------------

    [Test]
    public async Task Enrich_ComRepoVinculado_InjetaContextoNoSistema()
    {
        RequireGit();
        var origin = CriarOrigem("main");
        try
        {
            await _repos.OpenAsync("u1", "a/b", "main", origin, null, default);
            var request = new ChatCompletionRequest(
                "fake:1", [new ChatCompletionMessage("user", "liste os arquivos")]);
            var effective = await ChatPipeline.EnrichRequestAsync(
                request,
                new User { Id = "u1", Name = "U", Email = "u@x" },
                _db, _config, rag: null!, webSearch: null!, _repos, default);
            var system = effective.Messages.FirstOrDefault(m => m.Role == "system");
            Assert.That(system?.Content, Does.Contain("Repositório vinculado ao workspace: a/b")
                .And.Contain("branch 'main'"));
        }
        finally
        {
            Directory.Delete(origin, recursive: true);
        }
    }

    [Test]
    public async Task Enrich_SemRepo_NaoInjetaContexto()
    {
        var request = new ChatCompletionRequest(
            "fake:1", [new ChatCompletionMessage("user", "oi")]);
        var effective = await ChatPipeline.EnrichRequestAsync(
            request,
            new User { Id = "u2", Name = "U", Email = "u2@x" },
            _db, _config, rag: null!, webSearch: null!, _repos, default);
        Assert.That(
            effective.Messages.Where(m => m.Role == "system"),
            Has.None.Contains("Repositório vinculado"));
    }

    // ---------------- GitHubService: caminhos de erro ----------------

    [Test]
    public async Task ListRepos_RespostaNaoArray_RetornaVazio()
    {
        await _svc.SetTokenAsync("u1", "pat-1", default);
        _github.Body = "{}";
        Assert.That(await _svc.ListReposAsync("u1", default), Is.Empty);
    }

    [Test]
    public async Task ListRepos_ErroRede_RetornaVazio()
    {
        await _svc.SetTokenAsync("u1", "pat-1", default);
        _github.Throw = new HttpRequestException("boom");
        Assert.That(await _svc.ListReposAsync("u1", default), Is.Empty);
    }

    [Test]
    public async Task ListBranches_SemBranches_UsaDefault()
    {
        await _svc.SetTokenAsync("u1", "pat-1", default);
        _github.Body = "{\"default_branch\":\"trunk\"}";
        _github.BranchesBody = "[]";
        var branches = await _svc.ListBranchesAsync("u1", "a", "b", default);
        Assert.Multiple(() =>
        {
            Assert.That(branches!.DefaultBranch, Is.EqualTo("trunk"));
            Assert.That(branches.Branches, Is.EqualTo(["trunk"]));
        });
    }

    [Test]
    public async Task ListBranches_ErroRede_RetornaNull()
    {
        await _svc.SetTokenAsync("u1", "pat-1", default);
        _github.Throw = new HttpRequestException("boom");
        Assert.That(await _svc.ListBranchesAsync("u1", "a", "b", default), Is.Null);
    }

    [Test]
    public async Task ValidateToken_ErroRede_RetornaNull()
    {
        _github.Throw = new HttpRequestException("boom");
        Assert.That(await _svc.ValidateTokenAsync("pat", default), Is.Null);
    }

    // ---------------- WorkspaceRepoService: caminhos de erro ----------------

    [Test]
    public async Task Repo_Open_DiretorioComOutroConteudo_Erro()
    {
        var dir = Path.Join(_root, "data", "workspaces", "u1", "repos", "a__b");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, "outro.txt"), "x");

        var (binding, error) = await _repos.OpenAsync(
            "u1", "a/b", "main", "unused", null, default);
        Assert.Multiple(() =>
        {
            Assert.That(binding, Is.Null);
            Assert.That(error, Does.Contain("outro conteúdo"));
        });
    }

    [Test]
    public async Task Repo_Open_CheckoutExistente_BranchInexistente_Erro()
    {
        RequireGit();
        var origin = CriarOrigem("main");
        try
        {
            var (first, err1) = await _repos.OpenAsync(
                "u1", "a/b", "main", origin, null, default);
            Assert.That(err1, Is.Null);

            var (binding, error) = await _repos.OpenAsync(
                "u1", "a/b", "branch-fantasma", origin, null, default);
            Assert.Multiple(() =>
            {
                Assert.That(binding, Is.Null);
                Assert.That(error, Does.Contain("git fetch"));
            });
        }
        finally
        {
            Directory.Delete(origin, recursive: true);
        }
    }

    [Test]
    public async Task Repo_Open_CloneFalha_Erro()
    {
        var (binding, error) = await _repos.OpenAsync(
            "u1", "a/b", "main",
            Path.Join(_root, "origem-inexistente"), null, default);
        Assert.Multiple(() =>
        {
            Assert.That(binding, Is.Null);
            Assert.That(error, Does.StartWith("git clone:"));
        });
    }

    [Test]
    public async Task Repo_Open_Cancelado_PropagaOperacaoCancelada()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _repos.OpenAsync("u1", "a/b", "main", "unused", null, cts.Token));
    }

    // ---------------- Endpoints ----------------

    [Test]
    public async Task Endpoints_SemAuth_401()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        foreach (var uri in new[]
        {
            "/api/v1/github/config", "/api/v1/github/repos",
            "/api/v1/workspace/repo/",
        })
        {
            var response = await client.GetAsync(uri);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), uri);
        }
    }

    [Test]
    public async Task Endpoints_ConfigEBinding_FluxoBasico()
    {
        var dbPath = Path.Join(Path.GetTempPath(), $"openwebui-gh-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={dbPath}");
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        try
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/auths/signup", new SignUpRequest("GH", "gh@gh.local", "senha123"));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", auth.Token);

            // Sem token → configured=false; binding vazio.
            var cfg = await client.GetFromJsonAsync<GitHubConfigResponse>("/api/v1/github/config");
            Assert.That(cfg!.Configured, Is.False);
            var bound = await client.GetFromJsonAsync<WorkspaceRepoResponse>("/api/v1/workspace/repo/");
            Assert.That(bound!.Repo, Is.Null);

            // Token inválido → 400 (GitHub recusa ou indisponível).
            var put = await client.PutAsJsonAsync(
                "/api/v1/github/config", new GitHubTokenRequest("token-bogus-x"));
            Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

            // Slug inválido → 400; unbind → ok e continua null.
            var open = await client.PostAsJsonAsync("/api/v1/workspace/repo/open",
                new WorkspaceRepoOpenRequest("sem-barra", "main"));
            Assert.That(open.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            var del = await client.DeleteAsync("/api/v1/workspace/repo/");
            Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", null);
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    // ---------------- helpers ----------------

    private static void RequireGit()
    {
        try
        {
            Git(null, "--version");
        }
        catch (Exception)
        {
            Assert.Ignore("git indisponível neste ambiente.");
        }
    }

    /// <summary>Cria um repo local (remote <c>file://</c>) com 1 commit.</summary>
    private static string CriarOrigem(string branch, string? extraBranch = null)
    {
        var origin = Path.Join(Path.GetTempPath(), $"owui-origin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(origin);
        Git(origin, "init", "-b", branch);
        File.WriteAllText(Path.Join(origin, "readme.md"), "oi\n");
        Git(origin, "-c", "user.email=t@t", "-c", "user.name=t", "add", ".");
        Git(origin, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", "base");
        if (extraBranch is not null)
        {
            Git(origin, "branch", extraBranch);
        }
        return origin;
    }

    private static string? Git(string? workdir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (workdir is not null)
        {
            psi.WorkingDirectory = workdir;
        }
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(psi);
        process!.WaitForExit(15000);
        return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : null;
    }

    /// <summary>Stub da API do GitHub: /user, /user/repos, /repos/{o}/{r}*.</summary>
    private sealed class GitHubStub : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? LastAuth { get; private set; }
        public string? Body { get; set; }
        public string? BranchesBody { get; set; }
        public Exception? Throw { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuth = request.Headers.Authorization?.ToString();
            if (Throw is not null)
            {
                throw Throw;
            }
            var path = request.RequestUri!.AbsolutePath;
            var body = Body
                ?? (Status != HttpStatusCode.OK ? "{}"
                : path == "/user" ? "{\"login\":\"afonso\"}"
                : path == "/user/repos" ? """
                    [{"full_name":"afonsoft/open-webui","name":"open-webui","private":true,
                      "default_branch":"main","html_url":"https://github.com/afonsoft/open-webui",
                      "description":"fork dotnet","updated_at":"2026-10-08T00:00:00Z"},
                     {"full_name":"afonsoft/outro","name":"outro","private":false,
                      "default_branch":"dev","html_url":"https://github.com/afonsoft/outro",
                      "description":"repo 2","updated_at":null}]
                    """
                : path.EndsWith("/branches") ? (BranchesBody ?? "[{\"name\":\"main\"},{\"name\":\"dev\"}]")
                : "{\"default_branch\":\"main\"}");
            return Task.FromResult(new HttpResponseMessage(
                Status == HttpStatusCode.OK ? HttpStatusCode.OK : Status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class StubEnv(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
