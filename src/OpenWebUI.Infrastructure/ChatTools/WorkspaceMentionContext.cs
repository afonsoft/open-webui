using System.Text;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Expansão de menções <c>@path</c> do composer (SPEC-20261009-ide-mentions-tests,
/// RF-002). Cada chip vira um bloco <c>&lt;file path="…"&gt;conteúdo&lt;/file&gt;</c>
/// no prompt da run — o usuário vê o chip, nunca o dump.
/// Caps: 16KB por arquivo, 64KB no total; binário/oversize vira nota
/// placeholder; caminho fora do jail do workdir é descartado em silêncio.
/// </summary>
public static class WorkspaceMentionContext
{
    /// <summary>Cap de conteúdo por arquivo mencionado (16KB em chars).</summary>
    public const int MaxFileChars = 16 * 1024;

    /// <summary>Cap de conteúdo somando todos os blocos (64KB em chars).</summary>
    public const int MaxTotalChars = 64 * 1024;

    /// <summary>Máximo de menções processadas por envio (a UI oferece top-20).</summary>
    public const int MaxMentions = 20;

    /// <summary>Acima deste tamanho o arquivo vira placeholder sem ser aberto.</summary>
    private const long MaxReadableBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Monta os blocos <c>&lt;file&gt;</c> para os caminhos mencionados.
    /// Retorna null quando nenhum arquivo elegível é encontrado.
    /// </summary>
    /// <param name="workdir">Diretório de trabalho resolvido (jail).</param>
    /// <param name="paths">Caminhos relativos selecionados nos chips.</param>
    /// <param name="ct">Cancelamento.</param>
    public static async Task<string?> BuildAsync(
        string workdir, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;

        foreach (var raw in paths)
        {
            if (seen.Count >= MaxMentions)
            {
                break;
            }
            var rel = raw?.Trim();
            if (string.IsNullOrEmpty(rel) || !seen.Add(rel))
            {
                continue;
            }

            var full = WorkspaceFiles.ResolveInsideFinal(workdir, rel, out _);
            // Fora do jail, diretório ou inexistente: drop silencioso (RF-002).
            if (full is null || !File.Exists(full))
            {
                continue;
            }

            string body;
            var size = new FileInfo(full).Length;
            if (WorkspaceFiles.LooksBinary(full))
            {
                body = "[binary file — content omitted]";
            }
            else if (size > MaxReadableBytes)
            {
                body = "[file too large — content omitted]";
            }
            else
            {
                var text = await File.ReadAllTextAsync(full, ct);
                var truncated = false;
                var content = text;
                if (content.Length > MaxFileChars)
                {
                    content = content[..MaxFileChars];
                    truncated = true;
                }

                var remaining = MaxTotalChars - total;
                if (remaining <= 0)
                {
                    body = "[omitted — total context cap reached]";
                }
                else
                {
                    if (content.Length > remaining)
                    {
                        content = content[..remaining];
                        truncated = true;
                    }
                    body = truncated ? content + "\n[… truncated]" : content;
                }
            }

            sb.Append("<file path=\"").Append(EscapeAttr(rel)).Append("\">");
            sb.Append(body);
            sb.Append("</file>\n");
            total += body.Length;
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>Escapa o atributo <c>path</c> (&amp; &lt; &gt; &quot;) para não quebrar o XML do bloco.</summary>
    internal static string EscapeAttr(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
