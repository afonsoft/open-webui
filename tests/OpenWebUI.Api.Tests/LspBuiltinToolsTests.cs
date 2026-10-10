using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Lsp;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura das tools <c>builtin:lsp_*</c> (SPEC-20261009-lsp-diagnostics):
/// gates (LSP off, sem repo, path inexistente, extensão sem servidor),
/// fluxo feliz contra o fake python (symbols/hover/definition/diagnostics)
/// e formatação com cap.
/// </summary>
[TestFixture, IsolateEnvironment]
public class LspBuiltinToolsTests
{
    private string _root = null!;
    private string _workdir = null!;
    private string _fake = null!;
    private AppDbContext _db = null!;
    private MemoryCache _cache = null!;
    private ConfigService _config = null!;
    private WorkspaceRepoService _repos = null!;
    private LspService _lsp = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"lsptools-{Guid.NewGuid():N}");
        _workdir = Path.Join(_root, "data", "workspaces", "u1", "repos", "o__b");
        Directory.CreateDirectory(_workdir);
        _fake = Path.Join(_root, "fake-lsp.py");
        File.WriteAllText(_fake, FakeServer);

        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Join(_root, "t.db")}").Options);
        DatabaseMigrator.MigrateAsync(_db).GetAwaiter().GetResult();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _config = new ConfigService(_db, _cache);
        _repos = new WorkspaceRepoService(_config, new StubEnv(_root));
        _lsp = new LspService(new LspOptions
        {
            RequestTimeoutSeconds = 15,
            ShutdownTimeoutSeconds = 4,
            Servers = new Dictionary<string, LspServerSpec>
            {
                ["python"] = new LspServerSpec("python3", [_fake]),
            },
        }, NullLogger<LspService>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _lsp.DisposeAsync();
        _db.Dispose();
        _cache.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TestContext.Progress.WriteLine($"cleanup best-effort: {ex.Message}");
        }
    }

    private BuiltinToolContext Ctx() => new("u1", null, null, _workdir, _workdir);

    private async Task BindAsync() =>
        await _config.SetAsync("u:u1:workspace.repo",
            new WorkspaceRepoBinding("o/b", "main", "repos/o__b"));

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    // ---------- gates ----------

    [Test]
    public async Task Guard_LspDesabilitado()
    {
        await using var off = new LspService(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Lsp:Enabled"] = "false" }).Build(),
            NullLogger<LspService>.Instance);
        var tool = new LspDiagnosticsBuiltinTool(off, _repos);
        var result = await tool.ExecuteAsync(Args("{}"), Ctx(), default);
        Assert.That(result.Text, Does.Contain("LSP desabilitado"));
    }

    [Test]
    public async Task Guard_SemRepoVinculado()
    {
        var tool = new LspDiagnosticsBuiltinTool(_lsp, _repos);
        var result = await tool.ExecuteAsync(Args("{}"), Ctx(), default);
        Assert.That(result.Text, Does.Contain("Nenhum repositório vinculado"));
    }

    [Test]
    public async Task Symbols_ArquivoInexistente()
    {
        await BindAsync();
        var tool = new LspSymbolsBuiltinTool(_lsp, _repos);
        var result = await tool.ExecuteAsync(
            Args("{\"path\":\"none.py\"}"), Ctx(), default);
        Assert.That(result.Text, Does.Contain("não existe"));
    }

    [Test]
    public async Task Symbols_ExtensaoSemServidor()
    {
        await BindAsync();
        await File.WriteAllTextAsync(Path.Join(_workdir, "a.xyz"), "x");
        var tool = new LspSymbolsBuiltinTool(_lsp, _repos);
        var result = await tool.ExecuteAsync(
            Args("{\"path\":\"a.xyz\"}"), Ctx(), default);
        Assert.That(result.Text, Does.Contain("sem servidor LSP mapeado"));
    }

    [Test]
    public async Task Diagnostics_SemPathSemServidorRodando()
    {
        await BindAsync();
        var tool = new LspDiagnosticsBuiltinTool(_lsp, _repos);
        var result = await tool.ExecuteAsync(Args("{}"), Ctx(), default);
        Assert.That(result.Text, Does.Contain("Nenhum servidor LSP rodando"));
    }

    // ---------- fluxo feliz contra o fake ----------

    [Test]
    public async Task Symbols_Fake_DevolveListaComCap()
    {
        await BindAsync();
        await File.WriteAllTextAsync(Path.Join(_workdir, "a.py"), "x=1\n");
        var tool = new LspSymbolsBuiltinTool(_lsp, _repos);
        var result = await tool.ExecuteAsync(
            Args("{\"path\":\"a.py\"}"), Ctx(), default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Does.Contain("Sym00"));
            Assert.That(result.Text, Does.Contain("function"));
            Assert.That(result.Text, Does.Contain("a.py"));
        });
    }

    [Test]
    public async Task Hover_Fake_DevolveDocs()
    {
        await BindAsync();
        await File.WriteAllTextAsync(Path.Join(_workdir, "a.py"), "x=1\n");
        var tool = new LspHoverBuiltinTool(_lsp, _repos);
        var result = await tool.ExecuteAsync(
            Args("{\"path\":\"a.py\",\"line\":1,\"col\":1}"), Ctx(), default);
        Assert.That(result.Text, Is.EqualTo("sym docs"));
    }

    [Test]
    public async Task Definition_ResultNull_DevolveNenhumaLocalizacao()
    {
        await BindAsync();
        await File.WriteAllTextAsync(Path.Join(_workdir, "a.py"), "x=1\n");
        var tool = new LspDefinitionBuiltinTool(_lsp, _repos);
        var result = await tool.ExecuteAsync(
            Args("{\"path\":\"a.py\",\"line\":1,\"col\":1}"), Ctx(), default);
        Assert.That(result.Text, Is.EqualTo("Nenhuma localização."));
    }

    [Test]
    public async Task Diagnostics_ComPath_PublicaWarningDoFake()
    {
        await BindAsync();
        await File.WriteAllTextAsync(Path.Join(_workdir, "a.py"), "x=1\n");
        var tool = new LspDiagnosticsBuiltinTool(_lsp, _repos);
        var result = await tool.ExecuteAsync(
            Args("{\"path\":\"a.py\"}"), Ctx(), default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Does.Contain("aviso fake"));
            Assert.That(result.Text, Does.Contain("warning"));
        });
    }

    [Test]
    public async Task WorkspaceSymbols_SemQuery_ErroParametro()
    {
        await BindAsync();
        var tool = new LspWorkspaceSymbolsBuiltinTool(_lsp, _repos);
        var result = await tool.ExecuteAsync(Args("{}"), Ctx(), default);
        Assert.That(result.Text, Does.Contain("'query' é obrigatório"));
    }

    [Test]
    public async Task WorkspaceSymbols_ResultNull_NenhumSimbolo()
    {
        await BindAsync();
        var tool = new LspWorkspaceSymbolsBuiltinTool(_lsp, _repos);
        var result = await tool.ExecuteAsync(
            Args("{\"query\":\"Foo\",\"language\":\"python\"}"), Ctx(), default);
        Assert.That(result.Text, Does.Contain("Nenhum símbolo para 'Foo'"));
    }

    /// <summary>Mesmo fake dos LspClientTests (didOpen→publishDiagnostics).</summary>
    private const string FakeServer = """
        import sys, json, os
        inp, out = sys.stdin.buffer, sys.stdout.buffer
        def send(obj):
            b = json.dumps(obj).encode()
            out.write(b"Content-Length: %d\r\n\r\n" % len(b) + b)
            out.flush()
        def read():
            n = 0
            while True:
                line = inp.readline()
                if not line:
                    return None
                line = line.strip()
                if not line:
                    break
                if line.lower().startswith(b"content-length:"):
                    n = int(line.split(b":")[1])
            return inp.read(n)
        while True:
            raw = read()
            if raw is None:
                break
            msg = json.loads(raw)
            mid, method = msg.get("id"), msg.get("method")
            if method == "initialize":
                send({"jsonrpc": "2.0", "id": mid,
                      "result": {"capabilities": {"textDocumentSync": 1,
                                 "documentSymbolProvider": True, "hoverProvider": True}}})
            elif method == "shutdown":
                send({"jsonrpc": "2.0", "id": mid, "result": None})
            elif method == "exit":
                sys.exit(0)
            elif method == "textDocument/didOpen":
                send({"jsonrpc": "2.0", "method": "textDocument/publishDiagnostics",
                      "params": {"uri": msg["params"]["textDocument"]["uri"], "diagnostics": [
                        {"range": {"start": {"line": 0, "character": 0},
                                   "end": {"line": 0, "character": 5}},
                         "severity": 2, "message": "aviso fake", "source": "fake"}]}})
            elif method == "textDocument/documentSymbol":
                send({"jsonrpc": "2.0", "id": mid, "result": [
                    {"name": "Sym%02d" % i, "kind": 12,
                     "range": {"start": {"line": i, "character": 0},
                               "end": {"line": i, "character": 4}},
                     "selectionRange": {"start": {"line": i, "character": 0},
                                        "end": {"line": i, "character": 4}}}
                    for i in range(60)]})
            elif method == "textDocument/hover":
                send({"jsonrpc": "2.0", "id": mid,
                      "result": {"contents": {"kind": "plaintext", "value": "sym docs"}}})
            elif mid is not None:
                send({"jsonrpc": "2.0", "id": mid, "result": None})
        """;

    private sealed class StubEnv(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
