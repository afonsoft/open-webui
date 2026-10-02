using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Espelho de <c>wwwroot/_framework</c> em URLs sem extensão para que proxies
/// corporativos que bloqueiam downloads por extensão (.dat/.wasm/...) ou
/// inspecionam payloads binários não quebrem o boot do Blazor WASM (mesma
/// abordagem do KnowledgeHub/agent-harness). Anônimo — mesma exposição que
/// <see cref="StaticAssetsEndpointRouteBuilderExtensions.MapStaticAssets"/>;
/// estritamente read-only, nomes validados e confinados a <c>_framework</c>.
/// </summary>
public static partial class FrameworkAssetsEndpoints
{
    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex ValidName();

    [GeneratedRegex("^[a-z0-9]{1,10}$")]
    private static partial Regex ValidExt();

    /// <summary>Mapeia <c>GET /framework-assets/{stem}/{ext}</c> (raw e <c>?enc=b64</c>).</summary>
    public static RouteGroupBuilder MapFrameworkAssetsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/framework-assets").AllowAnonymous();

        // O sufixo bloqueado não pode aparecer na URL — o cliente manda
        // stem/ext como dois segmentos. ?enc=b64 devolve o payload em base64
        // como text/plain, derrotando filtros de extensão e sniffing de
        // conteúdo binário; o cliente revalida o SHA-256 contra o hash de
        // integridade do boot antes de entregar os bytes ao Blazor.
        group.MapGet("/{stem}/{ext}", Serve);

        return group;
    }

    private static IResult Serve(string stem, string ext, HttpContext http, IWebHostEnvironment env)
    {
        if (stem.Contains("..", StringComparison.Ordinal)
            || !ValidName().IsMatch(stem)
            || !ValidExt().IsMatch(ext))
        {
            return Results.NotFound();
        }

        var fileName = $"{stem}.{ext}";
        var root = env.WebRootFileProvider;
        var file = root.GetFileInfo($"_framework/{fileName}");
        if (!file.Exists || file.IsDirectory)
        {
            return Results.NotFound();
        }

        http.Response.Headers.CacheControl = "public,max-age=31536000,immutable";

        var enc = (string?)http.Request.Query["enc"];
        if (string.Equals(enc, "b64", StringComparison.Ordinal))
        {
            using var raw = file.CreateReadStream();
            using var buffer = new MemoryStream();
            raw.CopyTo(buffer);
            return Results.Text(
                Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length),
                "text/plain");
        }

        // Caches compartilhados (proxy corporativo) precisam chavear por Accept-Encoding.
        http.Response.Headers.Vary = "Accept-Encoding";
        var compressed = TryCompressed(root, fileName, http.Request.Headers.AcceptEncoding.ToString(), out var encoding);
        if (compressed is not null)
        {
            http.Response.Headers.ContentEncoding = encoding;
            return Results.File(compressed.CreateReadStream(), ContentType(ext));
        }
        return Results.File(file.CreateReadStream(), ContentType(ext));
    }

    private static string ContentType(string ext) => ext switch
    {
        "wasm" => "application/wasm",
        "json" => "application/json",
        "js" => "text/javascript",
        _ => "application/octet-stream"
    };

    private static IFileInfo? TryCompressed(
        IFileProvider root, string fileName, string acceptEncoding, out string encoding)
    {
        // Publish emite irmãos .br/.gz para cada asset; dev emite .gz.
        foreach (var (suffix, enc) in new[] { (".br", "br"), (".gz", "gzip") })
        {
            if (!acceptEncoding.Contains(enc, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var sibling = root.GetFileInfo($"_framework/{fileName}{suffix}");
            if (sibling.Exists && !sibling.IsDirectory)
            {
                encoding = enc;
                return sibling;
            }
        }
        encoding = "";
        return null;
    }
}
