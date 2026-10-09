using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Integração GitHub por usuário (SPEC-20261008-github-repo-workspace):
/// o PAT fica no kv <see cref="ConfigService"/> sob
/// <c>u:{userId}:github.token</c> — nunca retorna pela API; a tela só vê
/// <c>configured</c> + <c>login</c>. Token validado em <c>GET /user</c>
/// antes de persistir.
/// </summary>
public sealed class GitHubService(
    IHttpClientFactory httpFactory, ConfigService config, IMemoryCache? cache = null)
{
    /// <summary>Nome do <see cref="HttpClient"/> registrado no Program.cs.</summary>
    public const string HttpClientName = "github";

    private const string ApiBase = "https://api.github.com";

    private static string TokenKey(string userId) => $"u:{userId}:github.token";
    private static string LoginKey(string userId) => $"u:{userId}:github.login";

    /// <summary>Status da integração do usuário (token mascarado).</summary>
    public async Task<GitHubConfigResponse> GetStatusAsync(string userId, CancellationToken ct)
    {
        var token = await GetTokenAsync(userId, ct);
        if (token is null)
        {
            return new GitHubConfigResponse(false, null);
        }

        var login = await config.GetAsync<string?>(LoginKey(userId), null, ct);
        return new GitHubConfigResponse(true, login);
    }

    /// <summary>Token salvo (uso interno — nunca exposto em resposta).</summary>
    public Task<string?> GetTokenAsync(string userId, CancellationToken ct) =>
        config.GetAsync<string?>(TokenKey(userId), null, ct);

    /// <summary>
    /// Valida o token em <c>GET /user</c> e persiste; devolve o login ou
    /// null quando o GitHub recusa (401/403) ou a API está indisponível.
    /// </summary>
    public async Task<string?> SetTokenAsync(string userId, string token, CancellationToken ct)
    {
        var login = await ValidateTokenAsync(token, ct);
        if (login is null)
        {
            return null;
        }

        await config.SetAsync(TokenKey(userId), token, ct);
        await config.SetAsync(LoginKey(userId), login, ct);
        return login;
    }

    /// <summary>Remove token + login do usuário.</summary>
    public async Task ClearTokenAsync(string userId, CancellationToken ct)
    {
        await config.SetAsync<string?>(TokenKey(userId), null, ct);
        await config.SetAsync<string?>(LoginKey(userId), null, ct);
    }

    /// <summary>Valida um token em <c>GET /user</c>; devolve o login ou null.</summary>
    public async Task<string?> ValidateTokenAsync(string token, CancellationToken ct)
    {
        try
        {
            using var doc = await SendAsync(token, HttpMethod.Get, $"{ApiBase}/user", ct);
            return doc is null ? null
                : doc.RootElement.TryGetProperty("login", out var l) ? l.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Lista repositórios do usuário (mais recentes primeiro, até 100).</summary>
    public async Task<IReadOnlyList<GitHubRepoResponse>> ListReposAsync(
        string userId, CancellationToken ct)
    {
        var token = await GetTokenAsync(userId, ct);
        if (token is null)
        {
            return [];
        }

        try
        {
            using var doc = await SendAsync(token, HttpMethod.Get,
                $"{ApiBase}/user/repos?per_page=100&sort=updated&affiliation=owner,collaborator,organization_member", ct);
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var repos = new List<GitHubRepoResponse>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var fullName = el.TryGetProperty("full_name", out var fn) ? fn.GetString() : null;
                if (string.IsNullOrEmpty(fullName))
                {
                    continue;
                }

                repos.Add(new GitHubRepoResponse(
                    fullName,
                    el.TryGetProperty("name", out var n) ? n.GetString() ?? fullName : fullName,
                    el.TryGetProperty("private", out var p) && p.GetBoolean(),
                    el.TryGetProperty("default_branch", out var db) ? db.GetString() ?? "main" : "main",
                    el.TryGetProperty("html_url", out var h) ? h.GetString() ?? $"https://github.com/{fullName}" : $"https://github.com/{fullName}",
                    el.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                    el.TryGetProperty("updated_at", out var u) ? u.GetString() : null));
            }
            return repos;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// PRs abertos do repo vinculado com rollup de checks (SPEC-20261009-pr-ci-panel).
    /// Sem token ou token recusado (401/403) → <c>NeedsToken</c>; falha de rede →
    /// null (o endpoint responde <c>github:false</c>). Resultados OK ficam 60s em cache.
    /// </summary>
    public async Task<WorkspacePullsResponse?> ListPullRequestsAsync(
        string userId, string owner, string repo, CancellationToken ct)
    {
        var token = await GetTokenAsync(userId, ct);
        if (token is null)
        {
            return new WorkspacePullsResponse(false, true, []);
        }

        var cacheKey = $"github:pulls:{userId}:{owner}/{repo}";
        if (cache?.TryGetValue(cacheKey, out WorkspacePullsResponse? cached) == true
            && cached is not null)
        {
            return cached;
        }

        try
        {
            var (status, doc) = await SendStatusAsync(token, HttpMethod.Get,
                $"{ApiBase}/repos/{owner}/{repo}/pulls?state=open&per_page=30", ct);
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new WorkspacePullsResponse(false, true, []);
            }
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var tasks = doc.RootElement.EnumerateArray()
                .Select(pr => MapPullAsync(token, owner, repo, pr, ct))
                .ToList();
            var mapped = await Task.WhenAll(tasks);
            // Auth recusada no caminho dos checks (token revogado) → needsToken.
            if (mapped.Any(p => p is null))
            {
                return new WorkspacePullsResponse(false, true, []);
            }

            var response = new WorkspacePullsResponse(
                true, false, mapped.Select(p => p!).ToList());
            cache?.Set(cacheKey, response, TimeSpan.FromSeconds(60));
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Mapeia um PR da API; null quando a auth falha no caminho.</summary>
    private async Task<WorkspacePullResponse?> MapPullAsync(
        string token, string owner, string repo, JsonElement pr, CancellationToken ct)
    {
        var checks = EmptyChecks;
        var headObj = pr.TryGetProperty("head", out var h0) && h0.ValueKind == JsonValueKind.Object
            ? h0 : (JsonElement?)null;
        var sha = headObj is { } hh && hh.TryGetProperty("sha", out var s) ? s.GetString() : null;
        if (sha is not null)
        {
            var rollup = await FetchChecksAsync(token, owner, repo, sha, ct);
            if (rollup is null)
            {
                return null; // 401/403 no caminho dos checks
            }
            checks = rollup;
        }

        return new WorkspacePullResponse(
            pr.TryGetProperty("number", out var n) ? n.GetInt32() : 0,
            pr.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty,
            pr.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
                && u.TryGetProperty("login", out var l) ? l.GetString() : null,
            headObj is { } hb && hb.TryGetProperty("ref", out var r) ? r.GetString() : null,
            pr.TryGetProperty("updated_at", out var up) ? up.GetString() : null,
            pr.TryGetProperty("html_url", out var h)
                ? h.GetString() ?? string.Empty : string.Empty,
            pr.TryGetProperty("draft", out var d) && d.GetBoolean(),
            checks);
    }

    private static readonly WorkspacePrChecksResponse EmptyChecks =
        new(0, 0, 0, 0, "pending");

    /// <summary>
    /// Rollup de CI do commit: combined status + check runs (RF-002).
    /// null quando a auth é recusada; checks vazios quando o endpoint falha.
    /// </summary>
    private async Task<WorkspacePrChecksResponse?> FetchChecksAsync(
        string token, string owner, string repo, string sha, CancellationToken ct)
    {
        var passing = 0;
        var failing = 0;
        var pending = 0;

        var (statusCode, statusDoc) = await SendStatusAsync(token, HttpMethod.Get,
            $"{ApiBase}/repos/{owner}/{repo}/commits/{sha}/status", ct);
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return null;
        }
        if (statusDoc is not null
            && statusDoc.RootElement.TryGetProperty("statuses", out var statuses)
            && statuses.ValueKind == JsonValueKind.Array)
        {
            foreach (var st in statuses.EnumerateArray())
            {
                switch (st.TryGetProperty("state", out var s) ? s.GetString() : null)
                {
                    case "success": passing++; break;
                    case "failure" or "error": failing++; break;
                    default: pending++; break;
                }
            }
        }

        var (checksCode, checksDoc) = await SendStatusAsync(token, HttpMethod.Get,
            $"{ApiBase}/repos/{owner}/{repo}/commits/{sha}/check-runs?per_page=100", ct);
        if (checksCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return null;
        }
        if (checksDoc is not null
            && checksDoc.RootElement.TryGetProperty("check_runs", out var runs)
            && runs.ValueKind == JsonValueKind.Array)
        {
            foreach (var run in runs.EnumerateArray())
            {
                var runStatus = run.TryGetProperty("status", out var rs) ? rs.GetString() : null;
                var conclusion = run.TryGetProperty("conclusion", out var c)
                    && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                switch (runStatus == "completed" ? conclusion : null)
                {
                    case "success" or "neutral" or "skipped": passing++; break;
                    case "failure" or "cancelled" or "timed_out"
                        or "action_required" or "startup_failure": failing++; break;
                    default: pending++; break;
                }
            }
        }

        var total = passing + failing + pending;
        var state = failing > 0 ? "failure"
            : pending > 0 || total == 0 ? "pending"
            : "success";
        return new WorkspacePrChecksResponse(total, passing, failing, pending, state);
    }

    /// <summary>Branches do repositório (até 100) + a branch padrão.</summary>
    public async Task<GitHubBranchesResponse?> ListBranchesAsync(
        string userId, string owner, string repo, CancellationToken ct)
    {
        var token = await GetTokenAsync(userId, ct);
        if (token is null)
        {
            return null;
        }

        try
        {
            var defaultBranch = "main";
            using var repoDoc = await SendAsync(
                token, HttpMethod.Get, $"{ApiBase}/repos/{owner}/{repo}", ct);
            if (repoDoc is not null
                && repoDoc.RootElement.TryGetProperty("default_branch", out var db)
                && db.GetString() is { Length: > 0 } def)
            {
                defaultBranch = def;
            }

            var branches = new List<string>();
            using var doc = await SendAsync(token, HttpMethod.Get,
                $"{ApiBase}/repos/{owner}/{repo}/branches?per_page=100", ct);
            if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in doc.RootElement.EnumerateArray()
                             .Select(el => el.TryGetProperty("name", out var n) ? n.GetString() : null)
                             .Where(name => !string.IsNullOrEmpty(name)))
                {
                    branches.Add(name!);
                }
            }
            if (branches.Count == 0)
            {
                branches.Add(defaultBranch);
            }

            return new GitHubBranchesResponse(defaultBranch, branches);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Chamada autenticada à API do GitHub; null em não-2xx/timeout.</summary>
    private async Task<JsonDocument?> SendAsync(
        string token, HttpMethod method, string uri, CancellationToken ct)
    {
        var (_, doc) = await SendStatusAsync(token, method, uri, ct);
        return doc;
    }

    /// <summary><see cref="SendAsync"/> que também devolve o status HTTP (401/403 importam).</summary>
    private async Task<(HttpStatusCode Status, JsonDocument? Doc)> SendStatusAsync(
        string token, HttpMethod method, string uri, CancellationToken ct)
    {
        var client = httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("OpenWebUI-NET");
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return (response.StatusCode, null);
        }

        return (response.StatusCode,
            await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct));
    }
}
