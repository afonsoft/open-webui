using System.Text;

namespace OpenWebUI.Infrastructure.Lsp;

/// <summary>
/// Framing <c>Content-Length</c> do JSON-RPC sobre streams (LSP base spec):
/// header <c>Content-Length: N\r\n</c> (+ headers extras tolerados) seguido de
/// <c>\r\n\r\n</c> e exatamente N bytes de corpo UTF-8. A leitura tolera
/// chegada fatiada (chunked) e termina em EOF limpo retornando null.
/// </summary>
public static class LspFraming
{
    /// <summary>Grava uma mensagem: header + corpo (bytes UTF-8 contados).</summary>
    public static async Task WriteAsync(Stream output, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await output.WriteAsync(header, ct);
        await output.WriteAsync(body, ct);
        await output.FlushAsync(ct);
    }

    /// <summary>Atalho: serializa texto e grava.</summary>
    public static Task WriteAsync(Stream output, string body, CancellationToken ct) =>
        WriteAsync(output, Encoding.UTF8.GetBytes(body), ct);

    /// <summary>
    /// Lê a próxima mensagem completa. Retorna null no EOF limpo (sem dados
    /// pendentes); EOF no meio de header/corpo lança <see cref="EndOfStreamException"/>.
    /// </summary>
    public static async Task<byte[]?> ReadAsync(Stream input, CancellationToken ct)
    {
        var header = new List<byte>(128);
        var gotAny = false;
        var headerBuf = new byte[1];
        while (!EndsWithBlankLine(header))
        {
            var read = await input.ReadAsync(headerBuf.AsMemory(0, 1), ct);
            if (read == 0)
            {
                return gotAny
                    ? throw new EndOfStreamException("EOF no meio do header JSON-RPC.")
                    : null;
            }
            gotAny = true;
            header.Add(headerBuf[0]);
            if (header.Count > 8192)
            {
                throw new InvalidDataException("Header JSON-RPC acima de 8KB.");
            }
        }

        var length = ParseContentLength(header);
        var body = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await input.ReadAsync(body.AsMemory(offset, length - offset), ct);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"EOF no meio do corpo JSON-RPC ({offset}/{length} bytes).");
            }
            offset += read;
        }
        return body;
    }

    /// <summary>Header termina em <c>\r\n\r\n</c> (estrito) ou <c>\n\n</c> (tolerância).</summary>
    private static bool EndsWithBlankLine(List<byte> header)
    {
        var n = header.Count;
        return (n >= 4 && header[n - 4] == '\r' && header[n - 3] == '\n'
                && header[n - 2] == '\r' && header[n - 1] == '\n')
            || (n >= 2 && header[n - 2] == '\n' && header[n - 1] == '\n');
    }

    /// <summary>Extrai <c>Content-Length</c> do bloco de header (obrigatório).</summary>
    public static int ParseContentLength(IReadOnlyList<byte> headerBytes)
    {
        var text = Encoding.ASCII.GetString(headerBytes as byte[] ?? headerBytes.ToArray());
        var length = -1;
        foreach (var trimmed in text.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (trimmed.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(trimmed["Content-Length:".Length..].Trim(), out var n))
            {
                length = n;
            }
        }
        return length >= 0
            ? length
            : throw new InvalidDataException("Header JSON-RPC sem Content-Length.");
    }
}
