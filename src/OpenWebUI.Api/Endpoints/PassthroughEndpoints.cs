using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Routers gerenciados `/ollama/*` e `/openai/*`: proxy autenticado aos
/// provedores configurados, como no upstream (SDKs usam o Open WebUI
/// como gateway com auth JWT/sk-*).
/// </summary>
public static class PassthroughEndpoints
{
    public static void MapPassthroughEndpoints(this IEndpointRouteBuilder app)
    {
        var ollama = app.MapGroup("/ollama").RequireAuthorization();
        ollama.MapGet("/api/tags",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, "ollama", "api/tags", null));
        ollama.MapGet("/api/version",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, "ollama", "api/version", null));
        ollama.MapPost("/api/show",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, "ollama", "api/show", null));
        ollama.MapPost("/api/chat",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, "ollama", "api/chat", null));
        ollama.MapPost("/api/generate",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, "ollama", "api/generate", null));
        ollama.MapPost("/api/embed",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, "ollama", "api/embed", null));
        ollama.MapPost("/api/pull",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, "ollama", "api/pull", null));
        ollama.MapPost("/api/create",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, "ollama", "api/create", null));
        ollama.MapDelete("/api/delete",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, "ollama", "api/delete", null));
        ollama.MapPost("/api/copy",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, "ollama", "api/copy", null));
        ollama.MapMethods("/api/blobs/{digest}", ["GET", "HEAD"],
            (HttpContext http, string digest, ProviderProxyService proxy) =>
                ProxyBlob(http, proxy, digest));
        ollama.MapPost("/api/blobs/{digest}",
            (HttpContext http, string digest, ProviderProxyService proxy) =>
                ProxyBlob(http, proxy, digest, maxUpload: MaxBlobUpload));

        // Variantes indexadas: /ollama/{idx}/api/* usa a conexão de índice idx.
        var ollamaIndexed = app.MapGroup("/ollama/{idx:int}").RequireAuthorization();
        ollamaIndexed.MapGet("/api/tags",
            (HttpContext http, int idx, ProviderProxyService proxy) => Proxy(http, proxy, "ollama", "api/tags", idx));
        ollamaIndexed.MapPost("/api/chat",
            (HttpContext http, int idx, ProviderProxyService proxy) => Proxy(http, proxy, "ollama", "api/chat", idx));

        var openai = app.MapGroup("/openai").RequireAuthorization();
        openai.MapGet("/models",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, "openai", "models", null));
        openai.MapPost("/chat/completions",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, "openai", "chat/completions", null));
        openai.MapPost("/embeddings",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, "openai", "embeddings", null));
        openai.MapPost("/audio/speech",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, "openai", "audio/speech", null));
        openai.MapPost("/audio/transcriptions",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, "openai", "audio/transcriptions", null));
        openai.MapPost("/images/generations",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, "openai", "images/generations", null));

        var openaiIndexed = app.MapGroup("/openai/{idx:int}").RequireAuthorization();
        openaiIndexed.MapPost("/audio/speech",
            (HttpContext http, int idx, ProviderProxyService proxy) => Proxy(http, proxy, "openai", "audio/speech", idx));
        openaiIndexed.MapPost("/images/generations",
            (HttpContext http, int idx, ProviderProxyService proxy) => Proxy(http, proxy, "openai", "images/generations", idx));
        openaiIndexed.MapGet("/models",
            (HttpContext http, int idx, ProviderProxyService proxy) => Proxy(http, proxy, "openai", "models", idx));
        openaiIndexed.MapPost("/chat/completions",
            (HttpContext http, int idx, ProviderProxyService proxy) => Proxy(http, proxy, "openai", "chat/completions", idx));
    }

    /// <summary>Limite do upload de blob (512 MB, alinhado ao upstream).</summary>
    private const long MaxBlobUpload = 512L * 1024 * 1024;

    /// <summary>Regex do digest de blob aceito pelo Ollama (sha256 + 64 hex).</summary>
    private static readonly System.Text.RegularExpressions.Regex BlobDigest =
        new(@"^sha256:[0-9a-fA-F]{64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Valida o digest e o limite de upload antes de proxear blobs.</summary>
    private static Task<IResult> ProxyBlob(
        HttpContext http, ProviderProxyService proxy, string digest, long? maxUpload = null)
    {
        if (!BlobDigest.IsMatch(digest))
        {
            return Task.FromResult<IResult>(
                Results.BadRequest(new { detail = "Digest inválido (sha256:<hex64>)." }));
        }

        if (maxUpload is not null)
        {
            http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()
                ?.MaxRequestBodySize = maxUpload.Value;
        }

        return Proxy(http, proxy, "ollama", $"api/blobs/{digest}", null);
    }

    /// <summary>Lê o body inbound, delega ao serviço e converte o resultado em IResult.</summary>
    private static async Task<IResult> Proxy(
        HttpContext http, ProviderProxyService proxy, string provider, string path, int? index)
    {
        byte[]? body = null;
        if (http.Request.ContentLength is > 0 || http.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
            body = buffer.ToArray();
        }

        var result = await proxy.ForwardAsync(
            new HttpMethod(http.Request.Method), provider, path, index,
            body, http.Request.ContentType, http.Request.QueryString.Value, http.RequestAborted);
        return result.Response is null
            ? Results.Problem(result.Error, statusCode: result.StatusCode)
            : new ProxiedResult(result.Response);
    }

    /// <summary>Headers hop-by-hop que não são repassados ao cliente.</summary>
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "transfer-encoding", "te", "trailer",
        "proxy-authenticate", "proxy-authorization", "upgrade",
    };

    /// <summary>Result que copia status, headers e corpo (streaming) do upstream.</summary>
    private sealed class ProxiedResult(HttpResponseMessage response) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            using (response)
            {
                context.Response.StatusCode = (int)response.StatusCode;
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                {
                    if (!HopByHop.Contains(header.Key))
                    {
                        context.Response.Headers[header.Key] = header.Value.ToArray();
                    }
                }

                context.Response.Headers.Remove("transfer-encoding");
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
        }
    }
}
