using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Infrastructure.Lsp;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura de <see cref="JsonRpcPeer"/> (SPEC-20261009-lsp-diagnostics):
/// correlação request/resposta por id, erro JSON-RPC → LspRequestException,
/// dispatch de notificações, resposta padrão a requests do servidor
/// (workspace/configuration com array de nulls), descarte de não-JSON,
/// EOF falhando pendentes e falha rápida após queda.
/// </summary>
[TestFixture, IsolateEnvironment]
public class JsonRpcPeerTests
{
    private Pipe _toServer = null!;
    private Pipe _fromServer = null!;
    private JsonRpcPeer _peer = null!;

    [SetUp]
    public void SetUp()
    {
        _toServer = new Pipe();
        _fromServer = new Pipe();
        _peer = new JsonRpcPeer(
            _fromServer.Reader.AsStream(), _toServer.Writer.AsStream(),
            NullLogger.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _peer.DisposeAsync();
        await _toServer.Writer.CompleteAsync();
        await _toServer.Reader.CompleteAsync();
        await _fromServer.Writer.CompleteAsync();
        await _fromServer.Reader.CompleteAsync();
    }

    /// <summary>Lê um frame do "servidor" (o que o peer escreveu).</summary>
    private async Task<JsonDocument> ReadFromPeerAsync()
    {
        var body = await LspFraming.ReadAsync(
            _toServer.Reader.AsStream(), CancellationToken.None);
        Assert.That(body, Is.Not.Null, "nenhum frame recebido do peer");
        return JsonDocument.Parse(body!);
    }

    /// <summary>Escreve um frame do "servidor" (o que o peer lê).</summary>
    private async Task WriteToPeerAsync(string json)
    {
        await LspFraming.WriteAsync(
            _fromServer.Writer.AsStream(), Encoding.UTF8.GetBytes(json),
            CancellationToken.None);
    }

    [Test]
    public async Task Request_CorrelacionaRespostaPeloId()
    {
        var request = _peer.RequestAsync("textDocument/hover", new { x = 1 },
            TimeSpan.FromSeconds(10), CancellationToken.None);

        using var sent = await ReadFromPeerAsync();
        var id = sent.RootElement.GetProperty("id").GetInt64();
        Assert.That(sent.RootElement.GetProperty("method").GetString(),
            Is.EqualTo("textDocument/hover"));

        await WriteToPeerAsync(
            $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"ok\":true}}}}");
        var result = await request;
        Assert.That(result.GetProperty("ok").GetBoolean(), Is.True);
    }

    [Test]
    public async Task Request_ErroJsonRpc_ViraLspRequestException()
    {
        var request = _peer.RequestAsync("bad", null,
            TimeSpan.FromSeconds(10), CancellationToken.None);
        using var sent = await ReadFromPeerAsync();
        var id = sent.RootElement.GetProperty("id").GetInt64();

        await WriteToPeerAsync(
            $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":-32601,\"message\":\"not found\"}}}}");
        var ex = await Assert.ThrowsAsync<LspRequestException>(async () => await request);
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("not found"));
            Assert.That(ex.Message, Does.Contain("-32601"));
        });
    }

    [Test]
    public async Task Request_RespostaSemResult_DevolveNull()
    {
        var request = _peer.RequestAsync("shutdown", null,
            TimeSpan.FromSeconds(10), CancellationToken.None);
        using var sent = await ReadFromPeerAsync();
        var id = sent.RootElement.GetProperty("id").GetInt64();

        await WriteToPeerAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id}}}");
        var result = await request;
        Assert.That(result.ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public async Task Notification_EventoDespacha()
    {
        var got = new TaskCompletionSource<(string, JsonElement)>();
        _peer.Notification += (m, p) => got.TrySetResult((m, p));

        var request = _peer.RequestAsync("x", null,
            TimeSpan.FromSeconds(10), CancellationToken.None);
        using var sent = await ReadFromPeerAsync(); // garante reader rodando

        await WriteToPeerAsync(
            "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/publishDiagnostics\"," +
            "\"params\":{\"uri\":\"file:///a.py\"}}");
        var (method, prms) = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Multiple(() =>
        {
            Assert.That(method, Is.EqualTo("textDocument/publishDiagnostics"));
            Assert.That(prms.GetProperty("uri").GetString(), Is.EqualTo("file:///a.py"));
        });

        await WriteToPeerAsync(
            $"{{\"jsonrpc\":\"2.0\",\"id\":{sent.RootElement.GetProperty("id").GetInt64()},\"result\":null}}");
        await request;
    }

    [Test]
    public async Task ServerRequest_WorkspaceConfiguration_RespondeArrayDeNulls()
    {
        var request = _peer.RequestAsync("x", null,
            TimeSpan.FromSeconds(10), CancellationToken.None);
        using var sent = await ReadFromPeerAsync();

        await WriteToPeerAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":\"srv-1\",\"method\":\"workspace/configuration\"," +
            "\"params\":{\"items\":[{\"section\":\"a\"},{\"section\":\"b\"}]}}");

        using var reply = await ReadFromPeerAsync();
        Assert.Multiple(() =>
        {
            Assert.That(reply.RootElement.GetProperty("id").GetString(), Is.EqualTo("srv-1"));
            Assert.That(reply.RootElement.GetProperty("result").GetArrayLength(), Is.EqualTo(2));
            Assert.That(reply.RootElement.GetProperty("result")[0].ValueKind,
                Is.EqualTo(JsonValueKind.Null));
        });

        await WriteToPeerAsync(
            $"{{\"jsonrpc\":\"2.0\",\"id\":{sent.RootElement.GetProperty("id").GetInt64()},\"result\":null}}");
        await request;
    }

    [Test]
    public async Task ServerRequest_OutroMetodo_RespondeNull()
    {
        var request = _peer.RequestAsync("x", null,
            TimeSpan.FromSeconds(10), CancellationToken.None);
        using var sent = await ReadFromPeerAsync();

        await WriteToPeerAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"client/registerCapability\",\"params\":{}}");
        using var reply = await ReadFromPeerAsync();
        Assert.Multiple(() =>
        {
            Assert.That(reply.RootElement.GetProperty("id").GetInt64(), Is.EqualTo(9));
            Assert.That(reply.RootElement.GetProperty("result").ValueKind,
                Is.EqualTo(JsonValueKind.Null));
        });

        await WriteToPeerAsync(
            $"{{\"jsonrpc\":\"2.0\",\"id\":{sent.RootElement.GetProperty("id").GetInt64()},\"result\":null}}");
        await request;
    }

    [Test]
    public async Task MensagemNaoJson_DescartadaSemQuebrar()
    {
        var request = _peer.RequestAsync("x", null,
            TimeSpan.FromSeconds(10), CancellationToken.None);
        using var sent = await ReadFromPeerAsync();
        var id = sent.RootElement.GetProperty("id").GetInt64();

        await WriteToPeerAsync("isso não é json");
        await WriteToPeerAsync(
            $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":\"ok\"}}");
        var result = await request;
        Assert.That(result.GetString(), Is.EqualTo("ok"));
    }

    [Test]
    public async Task Notify_EscreveSemId()
    {
        await _peer.NotifyAsync("textDocument/didOpen",
            new { textDocument = new { uri = "file:///a" } }, CancellationToken.None);
        using var sent = await ReadFromPeerAsync();
        Assert.Multiple(() =>
        {
            Assert.That(sent.RootElement.GetProperty("method").GetString(),
                Is.EqualTo("textDocument/didOpen"));
            Assert.That(sent.RootElement.TryGetProperty("id", out _), Is.False);
        });
    }

    [Test]
    public async Task Eof_FalhaPendentesEProximasRequests()
    {
        var request = _peer.RequestAsync("x", null,
            TimeSpan.FromSeconds(30), CancellationToken.None);
        await ReadFromPeerAsync();

        await _fromServer.Writer.CompleteAsync(); // EOF no stream do servidor
        Assert.That(async () => await request, Throws.InstanceOf<IOException>());
        Assert.That(_peer.Failure, Is.Not.Null);
        Assert.That(
            async () => await _peer.RequestAsync("y", null,
                TimeSpan.FromSeconds(5), CancellationToken.None),
            Throws.InstanceOf<IOException>());
    }

    [Test]
    public void Timeout_RequestSemResposta_Estoura()
    {
        Assert.That(
            async () => await _peer.RequestAsync("never", null,
                TimeSpan.FromMilliseconds(300), CancellationToken.None),
            Throws.InstanceOf<TimeoutException>());
    }
}
