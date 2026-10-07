using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Captura screenshots de páginas web via browser headless
/// (SPEC-20261007-chat-agent-parity RF-017): invoca o Chrome/Chromium com
/// <c>--headless --screenshot</c> — zero dependências novas, mesmo espírito
/// do <c>PtySession</c> (spawn de processo do host). O binário é resolvido
/// por <c>BrowserTools:Path</c>/env <c>BROWSER_PATH</c>, depois por PATH
/// (google-chrome, chromium…) e por último no cache do Playwright
/// (<c>~/.cache/ms-playwright</c>). Nenhum browser →
/// <see cref="ResolvePath"/> retorna null e a tool avisa como habilitar.
/// </summary>
public sealed class BrowserScreenshotService(
    IConfiguration configuration,
    ILogger<BrowserScreenshotService> logger)
{
    /// <summary>Timeout total do processo headless.</summary>
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Viewport mínimo/máximo aceito.</summary>
    private const int MinDimension = 100;
    private const int MaxDimension = 4096;

    /// <summary>Nomes de binário tentados no PATH, em ordem de preferência.</summary>
    private static readonly string[] PathCandidates =
    [
        "google-chrome",
        "google-chrome-stable",
        "chromium",
        "chromium-browser",
        "chrome",
        "msedge",
        "headless_shell",
    ];

    /// <summary>Caminho do binário resolvido (cache — o PATH não muda).</summary>
    private string? _resolvedPath;
    private bool _resolved;

    /// <summary>
    /// Resolve o executável do browser: config/env primeiro, depois PATH e
    /// o cache do Playwright. Null quando nenhum está instalado.
    /// </summary>
    public string? ResolvePath()
    {
        if (_resolved)
        {
            return _resolvedPath;
        }

        var configured = configuration["BrowserTools:Path"];
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Cache(configured);
        }

        foreach (var name in PathCandidates)
        {
            var found = FindOnPath(name);
            if (found is not null)
            {
                return Cache(found);
            }
        }

        // Cache do Playwright: ~/.cache/ms-playwright/<browser>-*/... — pega
        // a revisão mais recente (ordenação desc por nome do diretório).
        var playwrightRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "ms-playwright");
        if (Directory.Exists(playwrightRoot))
        {
            foreach (var dir in Directory.GetDirectories(playwrightRoot)
                         .OrderByDescending(d => d, StringComparer.Ordinal))
            {
                foreach (var rel in new[]
                         {
                             Path.Combine("chrome-linux", "headless_shell"),
                             Path.Combine("chrome-linux", "chrome"),
                             Path.Combine("chrome-linux64", "chrome"),
                         })
                {
                    var candidate = Path.Combine(dir, rel);
                    if (File.Exists(candidate))
                    {
                        return Cache(candidate);
                    }
                }
            }
        }

        _resolved = true;
        _resolvedPath = null;
        return null;

        string? Cache(string path)
        {
            _resolved = true;
            _resolvedPath = path;
            logger.LogInformation("browser_screenshot: browser headless em {Path}", path);
            return path;
        }
    }

    /// <summary>
    /// Captura um screenshot PNG da URL no viewport pedido. Lança
    /// <see cref="InvalidOperationException"/> quando o browser falha ou
    /// excede o timeout — a tool traduz em texto de erro pro modelo.
    /// </summary>
    public async Task<byte[]> CaptureAsync(
        Uri uri, int width, int height, CancellationToken ct)
    {
        var browser = ResolvePath()
            ?? throw new InvalidOperationException(
                "Nenhum browser headless encontrado — instale chromium/google-chrome "
                + "ou aponte BrowserTools:Path (env BROWSER_PATH).");
        width = Math.Clamp(width, MinDimension, MaxDimension);
        height = Math.Clamp(height, MinDimension, MaxDimension);

        var output = Path.Combine(
            Path.GetTempPath(), $"webui-shot-{Guid.NewGuid():N}.png");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = browser,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--headless");
            psi.ArgumentList.Add("--disable-gpu");
            psi.ArgumentList.Add("--no-sandbox");
            psi.ArgumentList.Add("--disable-dev-shm-usage");
            psi.ArgumentList.Add("--hide-scrollbars");
            psi.ArgumentList.Add($"--screenshot={output}");
            psi.ArgumentList.Add($"--window-size={width},{height}");
            // Tempo máximo de "atividade virtual" para a página assentar
            // (JS/rede) antes do print — equivalente a um wait generoso.
            psi.ArgumentList.Add("--virtual-time-budget=15000");
            psi.ArgumentList.Add("--timeout=45000");
            psi.ArgumentList.Add(uri.ToString());

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Falha ao iniciar o browser headless.");
            try
            {
                await process.WaitForExitAsync(ct).WaitAsync(ProcessTimeout, ct);
            }
            catch (TimeoutException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                throw new InvalidOperationException(
                    $"Browser headless excedeu {ProcessTimeout.TotalSeconds}s sem responder.");
            }

            if (process.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
            {
                var stderr = (await process.StandardError.ReadToEndAsync(ct))
                    is { Length: > 0 } e ? e[..Math.Min(e.Length, 500)] : "(sem stderr)";
                throw new InvalidOperationException(
                    $"Browser saiu com código {process.ExitCode}: {stderr}");
            }

            return await File.ReadAllBytesAsync(output, ct);
        }
        finally
        {
            try { File.Delete(output); } catch { /* best effort */ }
        }
    }

    private static string? FindOnPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
