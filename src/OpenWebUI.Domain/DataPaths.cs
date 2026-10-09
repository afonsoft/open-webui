namespace OpenWebUI.Domain;

/// <summary>
/// Raiz dos dados mutáveis da aplicação (<c>{ContentRoot}/data</c> por padrão;
/// override via env <c>DATA_ROOT</c>, ex.: Docker aponta pro volume <c>/data</c>,
/// já que o content root <c>/app</c> pertence a root e o processo roda como
/// <c>app</c> uid 1654). Usada por uploads, workspaces, checkpoints e worktrees.
/// </summary>
public static class DataPaths
{
    /// <summary>Resolve a raiz de dados: <c>$DATA_ROOT</c> ou
    /// <c>{contentRootPath}/data</c>.</summary>
    public static string Root(string contentRootPath) =>
        Environment.GetEnvironmentVariable("DATA_ROOT") is { Length: > 0 } root
            ? root
            : Path.Join(contentRootPath, "data");
}
