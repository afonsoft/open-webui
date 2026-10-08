namespace OpenWebUI.Application.Contracts;

/// <summary>Status da integração GitHub do usuário (token nunca retorna).</summary>
/// <param name="Configured">Se há um token salvo.</param>
/// <param name="Login">Login do GitHub validado, quando configurado.</param>
public sealed record GitHubConfigResponse(bool Configured, string? Login);

/// <summary>Salva o token GitHub do usuário.</summary>
/// <param name="Token">Personal access token (repo scope).</param>
public sealed record GitHubTokenRequest(string? Token);

/// <summary>Repositório do usuário no GitHub.</summary>
/// <param name="FullName">Slug owner/repo.</param>
/// <param name="Name">Nome curto do repositório.</param>
/// <param name="Private">Visibilidade privada.</param>
/// <param name="DefaultBranch">Branch padrão.</param>
/// <param name="HtmlUrl">URL web do repositório.</param>
/// <param name="Description">Descrição opcional.</param>
/// <param name="UpdatedAt">Última atualização (ISO 8601).</param>
public sealed record GitHubRepoResponse(
    string FullName, string Name, bool Private, string DefaultBranch,
    string HtmlUrl, string? Description, string? UpdatedAt);

/// <summary>Branches de um repositório.</summary>
/// <param name="DefaultBranch">Branch padrão do repositório.</param>
/// <param name="Branches">Nomes das branches.</param>
public sealed record GitHubBranchesResponse(string DefaultBranch, IReadOnlyList<string> Branches);

/// <summary>Repositório vinculado ao workspace do usuário.</summary>
/// <param name="Repo">Slug owner/repo (null quando não vinculado).</param>
/// <param name="Branch">Branch aberta.</param>
/// <param name="Dir">Diretório do checkout dentro do workspace.</param>
public sealed record WorkspaceRepoResponse(string? Repo, string? Branch, string? Dir);

/// <summary>Vincula/abre um repositório + branch no workspace.</summary>
/// <param name="Repo">Slug owner/repo.</param>
/// <param name="Branch">Branch a abrir.</param>
public sealed record WorkspaceRepoOpenRequest(string? Repo, string? Branch);

/// <summary>Binding persistido do repositório do workspace (kv por usuário).</summary>
/// <param name="Repo">Slug owner/repo.</param>
/// <param name="Branch">Branch selecionada.</param>
/// <param name="Dir">Subdiretório do checkout dentro do workspace.</param>
public sealed record WorkspaceRepoBinding(string Repo, string Branch, string Dir);
