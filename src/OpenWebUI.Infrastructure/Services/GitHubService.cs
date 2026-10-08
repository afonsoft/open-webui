using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Integração GitHub por usuário (SPEC-20261008-github-repo-workspace):
/// o PAT fica no kv <see cref="ConfigService"/> sob
/// <c>u:{userId}:github.token</c> — nunca retorna pela API; a tela só vê
/// <c>configured</c> + <c>login</c>. Token validado em <c>GET /user</c>
/// antes de persistir.
/// </summary>
public sealed class GitHubService(IHttpClientFactory httpFactory, ConfigService config)
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
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                    {
                        branches.Add(name);
                    }
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
        var client = httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("OpenWebUI-NET");
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct);
    }
}
