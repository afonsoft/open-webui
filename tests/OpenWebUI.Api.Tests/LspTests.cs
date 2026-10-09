using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenWebUI.Infrastructure.Lsp;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261009-lsp-diagnostics (E16/S8): framing Content-Length (parse,
/// serialize, chunked), handshake initialize/shutdown contra um servidor
/// fake em python, cap de resposta, crash-restart com backoff+cap,
/// normalização path↔URI com espaços/unicode e degrade de linguagem sem
/// binário instalado.
/// </summary>
[TestFixture, IsolateEnvironment]
public class LspFramingTests
{
    [Test]
    public async Task Serializa_e_parseia_content_length()
    {
        using var stream = new MemoryStream();
        var payload = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1}");
        await LspFraming.WriteAsync(stream, payload, CancellationToken.None);

        stream.Position = 0;
        var read = await LspFraming.ReadAsync(stream, CancellationToken.None);
        Assert.That(read, Is.Not.Null);
        Assert.That(Encoding.UTF8.GetString(read!), Is.EqualTo("{\"jsonrpc\":\"2.0\",\"id\":1}"));
        // Header exato esperado.
        var head = Encoding.UTF8.GetString(stream.GetBuffer(), 0, 21);
        Assert.That(head, Does.StartWith("Content-Length: 24\r\n"));
    }

    [Test]
    public async Task Le_mensagem_entregue_em_pedacos()
    {
        var payload = Encoding.UTF8.GetBytes("{\"a\":\"" + new string('x', 500) + "\"}");
        using var stream = new MemoryStream();
        await LspFraming.WriteAsync(stream, payload, CancellationToken.None);
        var bytes = stream.ToArray();

        // Reescreve o stream entregando fatias de 7 bytes (header+body picados).
        using var chunked = new ChunkedStream(bytes, 7);
        var read = await LspFraming.ReadAsync(chunked, CancellationToken.None);
        Assert.That(read, Is.Not.Null);
        Assert.That(read!.Length, Is.EqualTo(payload.Length));
    }

    [Test]
    public async Task Eof_limpo_devolve_null_e_eof_no_meio_estoura()
    {
        using var empty = new MemoryStream();
        Assert.That(await LspFraming.ReadAsync(empty, CancellationToken.None), Is.Null);

        var partial = Encoding.UTF8.GetBytes("Content-Length: 99\r\n\r\n{\"a\":1");
        using var mid = new MemoryStream(partial);
        Assert.That(async () => await LspFraming.ReadAsync(mid, CancellationToken.None),
            Throws.InstanceOf<EndOfStreamException>());
    }

    [Test]
    public void Header_sem_content_length_estoura()
    {
        var bytes = Encoding.UTF8.GetBytes("Content-Type: x\r\n\r\n{}");
        using var s = new MemoryStream(bytes);
        Assert.That(async () => await LspFraming.ReadAsync(s, CancellationToken.None),
            Throws.InstanceOf<InvalidDataException>());
    }

    /// <summary>Stream que só libera N bytes por read (simula pipe).</summary>
    private sealed class ChunkedStream(byte[] data, int chunk) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, chunk), data.Length - _pos);
            if (n <= 0)
            {
                return 0;
            }
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin oo) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}

[TestFixture, IsolateEnvironment]
public class LspLanguageMapTests
{
    [TestCase("src/App.cs", "csharp", "csharp")]
    [TestCase("a/main.ts", "typescript", "typescript")]
    [TestCase("a/x.tsx", "typescript", "typescriptreact")]
    [TestCase("a/x.js", "javascript", "javascript")]
    [TestCase("a/x.jsx", "javascript", "javascriptreact")]
    [TestCase("a/x.py", "python", "python")]
    [TestCase("a/appsettings.json", "json", "json")]
    [TestCase("a/readme.md", null, null)]
    [TestCase("a/semext", null, null)]
    public void Extensao_mapeia_servidor_e_language_id(
        string path, string? server, string? languageId)
    {
        var lang = LspLanguageMap.ForPath(path);
        if (server is null)
        {
            Assert.That(lang, Is.Null);
            return;
        }
        Assert.That(lang!.ServerKey, Is.EqualTo(server));
        Assert.That(lang.LanguageId, Is.EqualTo(languageId));
    }

    [Test]
    public void Uri_normaliza_espacos_e_unicode()
    {
        var dir = Path.Join(TestContext.CurrentContext.WorkDirectory,
            "dir com espaço", "arquivo_ção.cs");
        var uri = LspClient.UriForPath(dir);
        Assert.That(uri, Does.StartWith("file://"));
        Assert.That(uri, Does.Not.Contain(" "));
        var back = LspClient.PathForUri(uri);
        Assert.That(back, Is.EqualTo(Path.GetFullPath(dir)));
    }

    [Test]
    public void PathForUri_ignora_uri_nao_file()
    {
        Assert.That(LspClient.PathForUri("https://x/y"), Is.Null);
        Assert.That(LspClient.PathForUri(null), Is.Null);
        Assert.That(LspClient.PathForUri("lixo"), Is.Null);
    }
}

[TestFixture, IsolateEnvironment]
public class LspOptionsTests
{
    [Test]
    public void Defaults_tem_mapa_das_4_linguagens()
    {
        var o = new LspOptions();
        Assert.That(o.Enabled, Is.True);
        Assert.That(o.RequestTimeoutSeconds, Is.EqualTo(30));
        Assert.That(o.MaxServersPerWorkdir, Is.EqualTo(2));
        Assert.That(o.Servers.Keys, Is.EquivalentTo(
            new[] { "csharp", "typescript", "javascript", "python", "json" }));
        Assert.That(o.Servers["csharp"].Command, Is.EqualTo("csharp-ls"));
    }

    [Test]
    public void Load_le_config_e_env_style()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Lsp:Enabled"] = "false",
                ["Lsp:RequestTimeoutSeconds"] = "5",
                ["Lsp:Servers:go:Command"] = "gopls",
                ["Lsp:Servers:go:Args"] = "serve",
            })
            .Build();
        var o = LspOptions.Load(cfg);
        Assert.That(o.Enabled, Is.False);
        Assert.That(o.RequestTimeoutSeconds, Is.EqualTo(5));
        Assert.That(o.Servers["go"].Command, Is.EqualTo("gopls"));
        Assert.That(o.Servers["go"].Args, Is.EqualTo(new[] { "serve" }));
        Assert.That(o.Servers.ContainsKey("csharp"), Is.True); // defaults preservados
    }
}

/// <summary>
/// Handshake/lifecycle contra o fake python (<c>fake-lsp.py</c>, gerado no
/// teste): responde initialize/publishDiagnostics/shutdown e morre quando
/// recebe a notificação <c>die</c>.
/// </summary>
[TestFixture, IsolateEnvironment]
public class LspClientTests
{
    private string _workdir = null!;
    private string _fake = null!;
    private LspOptions _options = null!;

    [SetUp]
    public void SetUp()
    {
        _workdir = Path.Join(TestContext.CurrentContext.WorkDirectory,
            "lsp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workdir);
        _fake = Path.Join(_workdir, "fake-lsp.py");
        File.WriteAllText(_fake, FakeServer);
        _options = new LspOptions
        {
            RequestTimeoutSeconds = 15,
            ShutdownTimeoutSeconds = 4,
            RestartBaseDelayMs = 10,
            Servers = new Dictionary<string, LspServerSpec>
            {
                ["fake"] = new LspServerSpec("python3", [_fake]),
                ["ghost"] = new LspServerSpec("binario-que-nao-existe-xyz", []),
            },
        };
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_workdir, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TestContext.Progress.WriteLine($"cleanup best-effort: {ex.Message}");
        }
    }

    private LspClient Client(string serverKey = "fake") =>
        new(_workdir, new LspLanguage(serverKey, serverKey),
            _options.Servers[serverKey], _options, NullLogger.Instance);

    [Test]
    public async Task Handshake_initialize_e_shutdown_limpo()
    {
        await using var client = Client();
        await client.EnsureRunningAsync(CancellationToken.None);
        Assert.That(client.State, Is.EqualTo(LspServerState.Running));

        // didOpen → servidor publica diagnostics do URI.
        var file = Path.Join(_workdir, "a.py");
        await File.WriteAllTextAsync(file, "print(1)\n");
        await client.DidOpenAsync(file, "print(1)\n", CancellationToken.None);
        var got = await client.AwaitDiagnosticsAsync(
            file, TimeSpan.FromSeconds(8), CancellationToken.None);
        Assert.That(got, Is.True);
        var snap = client.DiagnosticsSnapshot();
        Assert.That(snap[LspClient.UriForPath(file)].Count, Is.EqualTo(1));

        await client.DisposeAsync();
        Assert.That(client.State, Is.EqualTo(LspServerState.NotStarted));
    }

    [Test]
    public async Task Request_e_resposta_via_jsonrpc()
    {
        await using var client = Client();
        var result = await client.RequestAsync("textDocument/documentSymbol",
            new { textDocument = new { uri = "file:///x" } }, CancellationToken.None);
        Assert.That(result.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(result.GetArrayLength(), Is.EqualTo(60)); // 60 símbolos do fake
    }

    [Test]
    public async Task Binario_ausente_marca_unavailable()
    {
        await using var client = Client("ghost");
        Assert.That(async () => await client.EnsureRunningAsync(CancellationToken.None),
            Throws.InstanceOf<LspUnavailableException>());
        Assert.That(client.State, Is.EqualTo(LspServerState.Unavailable));
        Assert.That(client.Error, Does.Contain("binario-que-nao-existe-xyz"));
    }

    [Test]
    public async Task Crash_restarta_e_esgota_cap()
    {
        await using var client = Client();
        await client.EnsureRunningAsync(CancellationToken.None);
        Assert.That(client.State, Is.EqualTo(LspServerState.Running));

        var max = _options.MaxRestartAttempts;
        async Task KillAndWaitAsync()
        {
            await client.NotifyAsync("die", null, CancellationToken.None);
            var deadline = Environment.TickCount64 + 5000;
            while (client.State == LspServerState.Running
                && Environment.TickCount64 < deadline)
            {
                await Task.Delay(50);
            }
            Assert.That(client.State, Is.EqualTo(LspServerState.Crashed));
        }

        // Cada crash → EnsureRunning respawna (attempts 1..max).
        for (var i = 0; i < max; i++)
        {
            await KillAndWaitAsync();
            await client.EnsureRunningAsync(CancellationToken.None);
            Assert.That(client.State, Is.EqualTo(LspServerState.Running));
        }
        // Crash seguinte esgota o cap → unavailable.
        await KillAndWaitAsync();
        Assert.That(async () => await client.EnsureRunningAsync(CancellationToken.None),
            Throws.InstanceOf<LspUnavailableException>());
        Assert.That(client.State, Is.EqualTo(LspServerState.Unavailable));
    }

    [Test]
    public async Task Request_com_timeout_estoura()
    {
        _options = new LspOptions
        {
            RequestTimeoutSeconds = 1,
            ShutdownTimeoutSeconds = 4,
            Servers = _options.Servers,
        };
        await using var client = Client();
        await client.EnsureRunningAsync(CancellationToken.None);
        Assert.That(async () => await client.RequestAsync(
            "never-responds", null, CancellationToken.None),
            Throws.InstanceOf<TimeoutException>());
    }

    /// <summary>Servidor fake: LSP mínimo para os testes (didOpen→publishDiagnostics).</summary>
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
        last_uri = None
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
            elif method == "die":
                os._exit(2)
            elif method == "textDocument/didOpen":
                last_uri = msg["params"]["textDocument"]["uri"]
                send({"jsonrpc": "2.0", "method": "textDocument/publishDiagnostics",
                      "params": {"uri": last_uri, "diagnostics": [
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
            elif mid is not None and method == "never-responds":
                pass  # nunca responde — exercita o timeout da request
            elif mid is not None:
                send({"jsonrpc": "2.0", "id": mid, "result": None})
        """;
}

[TestFixture, IsolateEnvironment]
public class LspServiceTests
{
    private string _workdir = null!;

    [SetUp]
    public void SetUp()
    {
        _workdir = Path.Join(TestContext.CurrentContext.WorkDirectory,
            "lspsvc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workdir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_workdir, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TestContext.Progress.WriteLine($"cleanup best-effort: {ex.Message}");
        }
    }

    private LspService Service(LspOptions? options = null) =>
        new(options ?? new LspOptions(), NullLogger<LspService>.Instance);

    [Test]
    public async Task Linguagem_sem_binario_degrada_sem_quebrar()
    {
        await using var lsp = Service();
        var file = Path.Join(_workdir, "a.py");
        await File.WriteAllTextAsync(file, "x=1\n");
        // pylsp não está instalado na VM → unavailable, exceção tipada.
        Assert.That(
            async () => await lsp.GetClientAsync(_workdir,
                LspLanguageMap.ForPath(file)!, CancellationToken.None),
            Throws.InstanceOf<LspUnavailableException>());
        Assert.That(lsp.StateOf(_workdir, "python"), Is.EqualTo(LspServerState.Unavailable));
        // Sync APIs não propagam — didChange/didSave viram no-op.
        await lsp.NotifyFileWrittenAsync(_workdir, file, CancellationToken.None);
        await lsp.NotifyFileSavedAsync(_workdir, file, CancellationToken.None);
    }

    [Test]
    public async Task Desabilitado_recusa_client()
    {
        await using var lsp = Service(new LspOptions { Enabled = false });
        Assert.That(
            async () => await lsp.GetClientAsync(_workdir,
                new LspLanguage("python", "python"), CancellationToken.None),
            Throws.InstanceOf<LspUnavailableException>());
    }

    [Test]
    public async Task Cap_de_2_servidores_por_workdir()
    {
        var opts = new LspOptions
        {
            Servers = new Dictionary<string, LspServerSpec>
            {
                ["a"] = new("binario-ausente-a", []),
                ["b"] = new("binario-ausente-b", []),
                ["c"] = new("binario-ausente-c", []),
            },
        };
        await using var lsp = Service(opts);
        // Dois primeiros registram (mesmo indisponíveis ocupam slot).
        foreach (var k in new[] { "a", "b" })
        {
            try { await lsp.GetClientAsync(_workdir, new LspLanguage(k, k), CancellationToken.None); }
            catch (LspUnavailableException ex)
            {
                TestContext.Progress.WriteLine($"slot ocupado por server indisponível: {ex.Message}");
            }
        }
        Assert.That(
            async () => await lsp.GetClientAsync(_workdir,
                new LspLanguage("c", "c"), CancellationToken.None),
            Throws.InstanceOf<LspUnavailableException>()
                .With.Message.Contains("Cap"));
    }

    [Test]
    public async Task Diagnostics_sem_servidor_devolve_vazio()
    {
        await using var lsp = Service();
        var diags = lsp.Diagnostics(_workdir, null, out var running);
        Assert.That(diags, Is.Empty);
        Assert.That(running, Is.False);
    }
}

[TestFixture, IsolateEnvironment]
public class LspResponseTests
{
    [Test]
    public void Flatten_symbols_hierarquico_e_plano_com_cap()
    {
        // DocumentSymbol hierárquico (children) + SymbolInformation plano.
        var doc = JsonSerializer.SerializeToElement(new[]
        {
            new
            {
                name = "Cls", kind = 5,
                range = R(1, 0, 10, 0), selectionRange = R(1, 6, 1, 9),
                children = new[]
                {
                    new { name = "Run", kind = 6, range = R(2, 4, 4, 0),
                          selectionRange = R(2, 8, 2, 11) },
                },
            },
        });
        var list = LspResponse.FlattenSymbols(doc, "/w/a.cs", 50);
        Assert.That(list.Count, Is.EqualTo(2));
        Assert.That(list[0].Name, Is.EqualTo("Cls"));
        Assert.That(list[1].Name, Is.EqualTo("Run"));
        Assert.That(list[1].Container, Is.EqualTo("Cls"));
        Assert.That(list[1].Line, Is.EqualTo(2));

        var capped = LspResponse.FlattenSymbols(doc, "/w/a.cs", 1);
        Assert.That(capped.Count, Is.EqualTo(1));
    }

    [Test]
    public void Flatten_locations_cobre_location_e_locationlink()
    {
        var loc = JsonSerializer.SerializeToElement(new object[]
        {
            new { uri = "file:///w/a.cs", range = R(3, 2, 3, 7) },
            new { targetUri = "file:///w/b.cs",
                  targetSelectionRange = R(0, 0, 0, 3), targetRange = R(0, 0, 5, 0) },
        });
        var list = LspResponse.FlattenLocations(loc, 50);
        Assert.That(list.Count, Is.EqualTo(2));
        Assert.That(list[0].Path, Does.EndWith("a.cs"));
        Assert.That(list[0].Line, Is.EqualTo(3));
        Assert.That(list[1].Path, Does.EndWith("b.cs"));
    }

    [Test]
    public void Hover_extrai_markup_e_string_e_array()
    {
        var markup = JsonSerializer.SerializeToElement(
            new { contents = new { kind = "markdown", value = "**doc**" } });
        Assert.That(LspResponse.HoverText(markup), Is.EqualTo("**doc**"));

        var str = JsonSerializer.SerializeToElement(new { contents = "plain" });
        Assert.That(LspResponse.HoverText(str), Is.EqualTo("plain"));

        var arr = JsonSerializer.SerializeToElement(
            new { contents = new object[] { "a", new { value = "b" } } });
        Assert.That(LspResponse.HoverText(arr), Is.EqualTo("a\nb"));

        var vazio = JsonSerializer.SerializeToElement(new { contents = "" });
        Assert.That(LspResponse.HoverText(vazio), Is.Null);
    }

    [Test]
    public void Format_locations_respeita_cap()
    {
        var many = JsonSerializer.SerializeToElement(
            Enumerable.Range(0, 60).Select(i => new
            {
                uri = $"file:///w/f{i}.cs",
                range = R(i, 0, i, 1),
            }).ToArray());
        var text = LspResponse.FormatLocations(many, "/w", 50);
        Assert.That(text.Split('\n').Length, Is.EqualTo(50));
        Assert.That(text, Does.Contain("f0.cs:1:1"));
    }

    private static object R(int sl, int sc, int el, int ec) => new
    {
        start = new { line = sl, character = sc },
        end = new { line = el, character = ec },
    };
}
