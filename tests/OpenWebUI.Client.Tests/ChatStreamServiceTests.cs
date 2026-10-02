using System.Net;
using System.Text;
using Microsoft.JSInterop;
using OpenWebUI.Client.Services;

namespace OpenWebUI.Client.Tests;

/// <summary>Testes do consumo SSE de /api/chat/completions (deltas de chat).</summary>
[TestFixture]
public class ChatStreamServiceTests
{
    [Test]
    public async Task Stream_ChunkComChoicesVazio_NaoLancaEIgnora()
    {
        // Gateways OpenAI-compatíveis (ex.: OmniRoute auto/*) emitem chunks
        // sem choice — só role/usage/keepalive. Indexar choices[0] num array
        // vazio lançava ArgumentOutOfRangeException no WASM.
        var sse = string.Join('\n',
            "data: {\"choices\":[]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Olá\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\" mundo\"}}]}",
            "",
            "data: {\"choices\":[],\"usage\":{\"total_tokens\":10}}",
            "",
            "data: [DONE]",
            "");

        var service = CreateService(sse);

        var deltas = new List<string>();
        await foreach (var delta in service.StreamCompletionAsync(
            new OpenWebUI.Application.Contracts.ChatCompletionRequest("m", [])))
        {
            deltas.Add(delta);
        }

        Assert.That(deltas, Is.EqualTo(new[] { "Olá", " mundo" }));
    }

    [Test]
    public void Stream_ChunkDeErro_LancaInvalidOperation()
    {
        var sse = "data: {\"error\":\"provider caiu\"}\n\ndata: [DONE]\n";
        var service = CreateService(sse);

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in service.StreamCompletionAsync(
                new OpenWebUI.Application.Contracts.ChatCompletionRequest("m", [])))
            {
            }
        });
    }

    private static ChatStreamService CreateService(string sseBody)
    {
        var http = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream"),
            })))
        {
            BaseAddress = new Uri("http://localhost/"),
        };

        var js = new FakeJs();
        var auth = new AuthService(http, new BrowserStorage(js), new LocalizationService(http, js));
        return new ChatStreamService(http, auth);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private sealed class FakeJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
            => new();
    }
}
