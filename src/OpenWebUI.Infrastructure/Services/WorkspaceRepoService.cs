using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Hosting;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;

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

    /// <summary>
    /// Binding por chat (SPEC-20261010-chat-repo-binding): precede o binding
    /// global do usuário — cada chat pode trabalhar num repo diferente
    /// (estilo opencode-web, uma session por projeto).
    /// </summary>
    private static string ChatBindingKey(string chatId) => $"chat:{chatId}:workspace.repo";

    /// <summary>Raiz do workspace do usuário (jail das tools). Respeita
    /// <see cref="DataPaths.Root"/> (env <c>DATA_ROOT</c> — no Docker, /data).</summary>
    public string WorkspaceRoot(string userId) =>
        Path.Join(DataPaths.Root(env.ContentRootPath), "workspaces", userId);

    /// <summary>Binding atual (null quando não há repo vinculado).</summary>
    public Task<WorkspaceRepoBinding?> GetBindingAsync(string userId, CancellationToken ct) =>
        config.GetAsync<WorkspaceRepoBinding?>(BindingKey(userId), null, ct);

    /// <summary>Binding por chat (null quando o chat não tem repo próprio).</summary>
    public Task<WorkspaceRepoBinding?> GetChatBindingAsync(string chatId, CancellationToken ct) =>
        config.GetAsync<WorkspaceRepoBinding?>(ChatBindingKey(chatId), null, ct);

    /// <summary>Define ou limpa (null) o binding por chat.</summary>
    public Task SetChatBindingAsync(
        string chatId, WorkspaceRepoBinding? binding, CancellationToken ct) =>
        config.SetAsync(ChatBindingKey(chatId), binding, ct);

    /// <summary>
    /// Binding efetivo com a origem: <c>chat</c> → <c>user</c> → <c>none</c>.
    /// </summary>
    public async Task<(WorkspaceRepoBinding? Binding, string Source)> ResolveBindingAsync(
        string userId, string? chatId, CancellationToken ct)
    {
        if (chatId is not null
            && await GetChatBindingAsync(chatId, ct) is { } chatBinding)
        {
            return (chatBinding, "chat");
        }
        var userBinding = await GetBindingAsync(userId, ct);
        return (userBinding, userBinding is not null ? "user" : "none");
    }

    /// <summary>
    /// Diretório de trabalho efetivo: o checkout do repo vinculado quando
    /// existe, senão a raiz do workspace.
    /// </summary>
    public Task<string> ResolveWorkdirAsync(string userId, CancellationToken ct) =>
        ResolveWorkdirAsync(userId, null, ct);

    /// <summary>
    /// Igual a <see cref="ResolveWorkdirAsync(string, CancellationToken)"/>
    /// resolvendo primeiro o binding por chat (SPEC-20261010-chat-repo-binding).
    /// </summary>
    public async Task<string> ResolveWorkdirAsync(
        string userId, string? chatId, CancellationToken ct)
    {
        var (binding, _) = await ResolveBindingAsync(userId, chatId, ct);
        if (binding is null)
        {
            return WorkspaceRoot(userId);
        }

        var dir = Path.Join(WorkspaceRoot(userId), binding.Dir);
        return Directory.Exists(Path.Join(dir, ".git")) ? dir : WorkspaceRoot(userId);
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

        var (dir, absDir, error) = await EnsureCheckoutAsync(
            userId, slug, branch, cloneUrl, token, ct);
        if (error is not null)
        {
            return (null, error);
        }

        // TestCommand/FormatCommand customizados sobrevivem a re-open/branch switch (RF-003).
        var previous = await GetBindingAsync(userId, ct);
        var binding = new WorkspaceRepoBinding(
            slug, branch, dir, previous?.TestCommand, previous?.FormatCommand);
        await config.SetAsync(BindingKey(userId), binding, ct);
        // Bind/branch novo → cache de skills/commands do workdir expira na hora.
        SkillDiscoveryService.Invalidate(absDir);
        return (binding, null);
    }

    /// <summary>
    /// Igual a <see cref="OpenAsync"/> gravando o binding por chat
    /// (SPEC-20261010-chat-repo-binding). O checkout é compartilhado —
    /// diretório determinístico por slug, então vários chats (e o binding
    /// global) apontam para a mesma pasta sem clonar de novo.
    /// </summary>
    public async Task<(WorkspaceRepoBinding? Binding, string? Error)> OpenChatAsync(
        string userId, string chatId, string slug, string branch,
        string cloneUrl, string? token, CancellationToken ct)
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

        var (dir, absDir, error) = await EnsureCheckoutAsync(
            userId, slug, branch, cloneUrl, token, ct);
        if (error is not null)
        {
            return (null, error);
        }

        // Comandos customizados: herda do binding de chat anterior, caindo
        // no global do usuário quando o chat nunca teve repo próprio.
        var previous = await GetChatBindingAsync(chatId, ct)
            ?? await GetBindingAsync(userId, ct);
        var binding = new WorkspaceRepoBinding(
            slug, branch, dir, previous?.TestCommand, previous?.FormatCommand);
        await config.SetAsync(ChatBindingKey(chatId), binding, ct);
        SkillDiscoveryService.Invalidate(absDir);
        return (binding, null);
    }

    /// <summary>
    /// Garante o checkout do repo no workspace (clone shallow ou
    /// fetch+switch de branch no existente). Devolve o dir relativo e o
    /// caminho absoluto; <paramref name="error"/> preenchido em falha.
    /// </summary>
    private async Task<(string Dir, string AbsDir, string? Error)> EnsureCheckoutAsync(
        string userId, string slug, string branch,
        string cloneUrl, string? token, CancellationToken ct)
    {
        var name = slug[(slug.IndexOf('/') + 1)..];
        var dir = $"repos/{Sanitize(slug[..slug.IndexOf('/')])}__{Sanitize(name)}";
        var root = WorkspaceRoot(userId);
        var absDir = Path.Join(root, dir);

        Directory.CreateDirectory(root);
        string? error;
        if (Directory.Exists(Path.Join(absDir, ".git")))
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
            return (dir, absDir, $"Diretório '{dir}' já existe com outro conteúdo.");
        }
        else
        {
            error = await CloneAsync(absDir, cloneUrl, branch, token, ct);
        }

        return (dir, absDir, error);
    }

    /// <summary>Desvincula o repositório (o checkout permanece no disco).</summary>
    public async Task UnbindAsync(string userId, CancellationToken ct) =>
        await config.SetAsync<WorkspaceRepoBinding?>(BindingKey(userId), null, ct);

    /// <summary>
    /// Define ou limpa o comando de teste customizado do binding
    /// (override da detecção por manifesto — SPEC-20261009-ide-mentions-tests RF-003).
    /// </summary>
    public async Task SetTestCommandAsync(string userId, string? testCommand, CancellationToken ct)
    {
        var binding = await GetBindingAsync(userId, ct);
        if (binding is null)
        {
            return;
        }
        await config.SetAsync(BindingKey(userId),
            binding with { TestCommand = string.IsNullOrWhiteSpace(testCommand) ? null : testCommand.Trim() }, ct);
    }

    /// <summary>
    /// Igual a <see cref="SetTestCommandAsync(string, string?, CancellationToken)"/>
    /// mas resolve o binding efetivo do chat (SPEC-20261010-workspace-chatid-scope):
    /// escreve na chave <c>chat:{id}</c> quando a origem é o chat, senão na do usuário.
    /// </summary>
    public async Task SetTestCommandAsync(
        string userId, string? chatId, string? testCommand, CancellationToken ct)
    {
        var (binding, source) = await ResolveBindingAsync(userId, chatId, ct);
        if (binding is null)
        {
            return;
        }
        var updated = binding with
        {
            TestCommand = string.IsNullOrWhiteSpace(testCommand) ? null : testCommand.Trim(),
        };
        await config.SetAsync(
            source == "chat" ? ChatBindingKey(chatId!) : BindingKey(userId), updated, ct);
    }

    /// <summary>
    /// Define ou limpa o format hook por-repo do binding
    /// (override do global <c>Format:Command</c> — SPEC-20261009-worktree-format-hooks RF-004).
    /// </summary>
    public async Task SetFormatCommandAsync(string userId, string? formatCommand, CancellationToken ct)
    {
        var binding = await GetBindingAsync(userId, ct);
        if (binding is null)
        {
            return;
        }
        await config.SetAsync(BindingKey(userId),
            binding with { FormatCommand = string.IsNullOrWhiteSpace(formatCommand) ? null : formatCommand.Trim() }, ct);
    }

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
            psi.ArgumentList.Add(
                "http.https://github.com/.extraheader=AUTHORIZATION: basic "
                + Convert.ToBase64String(Encoding.ASCII.GetBytes($"x-access-token:{token}")));
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
        catch (OperationCanceledException oce)
        {
            // Normaliza TaskCanceledException → OperationCanceledException (cancelamento do request propaga).
            throw new OperationCanceledException(oce.Message, oce, ct);
        }
        catch (Win32Exception ex) { return (-1, string.Empty, ex.Message); }
        catch (ObjectDisposedException ex) { return (-1, string.Empty, ex.Message); }
        catch (InvalidOperationException ex) { return (-1, string.Empty, ex.Message); }
        catch (IOException ex) { return (-1, string.Empty, ex.Message); }
        catch (TimeoutException ex) { return (-1, string.Empty, ex.Message); }
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
