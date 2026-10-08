using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Hosting;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Vínculo repositório+branch ↔ workspace do usuário
/// (SPEC-20261008-github-repo-workspace): o checkout mora em
/// <c>data/workspaces/{userId}/repos/{owner}__{repo}</c> — dentro do jail
/// das tools <c>file_*</c>/<c>shell_exec</c>. O binding vai no kv por
/// usuário; o token nunca é persistido no remote do git (clone/fetch
/// autenticam via <c>http.extraheader</c>).
/// </summary>
public sealed class WorkspaceRepoService(ConfigService config, IHostEnvironment env)
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(3);

    private static string BindingKey(string userId) => $"u:{userId}:workspace.repo";

    /// <summary>Raiz do workspace do usuário (jail das tools).</summary>
    public string WorkspaceRoot(string userId) =>
        Path.Combine(env.ContentRootPath, "data", "workspaces", userId);

    /// <summary>Binding atual (null quando não há repo vinculado).</summary>
    public Task<WorkspaceRepoBinding?> GetBindingAsync(string userId, CancellationToken ct) =>
        config.GetAsync<WorkspaceRepoBinding?>(BindingKey(userId), null, ct);

    /// <summary>
    /// Diretório de trabalho efetivo: o checkout do repo vinculado quando
    /// existe, senão a raiz do workspace.
    /// </summary>
    public async Task<string> ResolveWorkdirAsync(string userId, CancellationToken ct)
    {
        var binding = await GetBindingAsync(userId, ct);
        if (binding is null)
        {
            return WorkspaceRoot(userId);
        }

        var dir = Path.Combine(WorkspaceRoot(userId), binding.Dir);
        return Directory.Exists(Path.Combine(dir, ".git")) ? dir : WorkspaceRoot(userId);
    }

    /// <summary>
    /// Abre (clone ou troca de branch) o repositório no workspace.
    /// <paramref name="cloneUrl"/> permite testes com remote <c>file://</c>;
    /// produção passa <c>https://github.com/{slug}.git</c>. Devolve o
    /// binding persistido ou erro amigável.
    /// </summary>
    public async Task<(WorkspaceRepoBinding? Binding, string? Error)> OpenAsync(
        string userId, string slug, string branch, string cloneUrl, string? token, CancellationToken ct)
    {
        if (!IsValidSlug(slug))
        {
            return (null, "Repositório inválido — use o formato owner/repo.");
        }
        if (string.IsNullOrWhiteSpace(branch) || branch.Any(char.IsWhiteSpace)
            || branch.StartsWith('-'))
        {
            return (null, "Branch inválida.");
        }

        var name = slug[(slug.IndexOf('/') + 1)..];
        var dir = $"repos/{Sanitize(slug[..slug.IndexOf('/')])}__{Sanitize(name)}";
        var root = WorkspaceRoot(userId);
        var absDir = Path.Combine(root, dir);

        Directory.CreateDirectory(root);
        string? error;
        if (Directory.Exists(Path.Combine(absDir, ".git")))
        {
            // Checkout existente: só troca de branch (fetch + switch + pull).
            // Refspec explícito — clones --single-branch não criam refs/remotes
            // das outras branches, e o switch --track depende dela.
            error = await RunGitLoggedAsync(absDir, ct, token,
                "fetch", "origin", "--depth", "50",
                $"+refs/heads/{branch}:refs/remotes/origin/{branch}");
            error ??= await SwitchBranchAsync(absDir, branch, token, ct);
        }
        else if (Directory.Exists(absDir) && Directory.EnumerateFileSystemEntries(absDir).Any())
        {
            return (null, $"Diretório '{dir}' já existe com outro conteúdo.");
        }
        else
        {
            error = await CloneAsync(absDir, cloneUrl, branch, token, ct);
        }

        if (error is not null)
        {
            return (null, error);
        }

        var binding = new WorkspaceRepoBinding(slug, branch, dir);
        await config.SetAsync(BindingKey(userId), binding, ct);
        return (binding, null);
    }

    /// <summary>Desvincula o repositório (o checkout permanece no disco).</summary>
    public async Task UnbindAsync(string userId, CancellationToken ct) =>
        await config.SetAsync<WorkspaceRepoBinding?>(BindingKey(userId), null, ct);

    private static async Task<string?> CloneAsync(
        string absDir, string url, string branch, string? token, CancellationToken ct)
    {
        var error = await RunGitLoggedAsync(null, ct, token,
            "clone", "--depth", "50", "--branch", branch, "--single-branch",
            url, absDir);
        if (error is not null)
        {
            return error;
        }

        // Limpa a URL gravada no clone (nunca carrega credencial).
        var uri = new Uri(url);
        var clean = uri.Scheme is "http" or "https"
            ? $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}"
            : url;
        return await RunGitLoggedAsync(absDir, ct, null,
            "remote", "set-url", "origin", clean);
    }

    private static async Task<string?> SwitchBranchAsync(
        string absDir, string branch, string? token, CancellationToken ct)
    {
        // Branch já existe localmente → switch; senão cria a partir de origin.
        var local = await RunGitAsync(absDir, ct,
            "rev-parse", "--verify", $"refs/heads/{branch}");
        var error = local is not null
            ? await RunGitLoggedAsync(absDir, ct, null, "switch", branch)
            : await RunGitLoggedAsync(absDir, ct, null,
                "switch", "-c", branch, $"refs/remotes/origin/{branch}");
        error ??= await RunGitLoggedAsync(absDir, ct, token,
            "pull", "--ff-only", "origin", branch);
        return error;
    }

    /// <summary>Roda git e devolve stderr enxuto em falha (sem token no texto).</summary>
    private static async Task<string?> RunGitLoggedAsync(
        string? workdir, CancellationToken ct, string? token, params string[] args)
    {
        var (code, _, err) = await RunAsync(workdir, ct, token, args);
        if (code == 0)
        {
            return null;
        }

        var text = (err ?? string.Empty).Trim();
        var first = text.Split('\n').FirstOrDefault(l => l.Length > 0) ?? "falha no git";
        return $"git {args[0]}: {first}";
    }

    private static Task<string?> RunGitAsync(string workdir, CancellationToken ct, params string[] args) =>
        RunAsync(workdir, ct, null, args)
            .ContinueWith(t => t.Result.ExitCode == 0 ? t.Result.Stdout : null, ct);

    private static async Task<(int ExitCode, string Stdout, string? Stderr)> RunAsync(
        string? workdir, CancellationToken ct, string? token, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workdir is not null)
        {
            psi.WorkingDirectory = workdir;
        }
        // Credencial só no header dessa invocação — nada é gravado em .git/config.
        if (token is not null)
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"http.https://github.com/.extraheader=AUTHORIZATION: bearer {token}");
        }
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).WaitAsync(GitTimeout, ct);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    private static bool IsValidSlug(string slug)
    {
        var parts = slug.Split('/');
        return parts.Length == 2
            && parts.All(p => p.Length > 0 && p.Length <= 100
                && p.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }
        return sb.ToString();
    }
}
