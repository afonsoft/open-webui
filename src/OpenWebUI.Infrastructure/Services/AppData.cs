using Microsoft.Extensions.Hosting;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Raiz dos dados de runtime do app (uploads, workspaces, checkpoints,
/// worktrees). Em dev é <c>{ContentRootPath}/data</c>; em container o
/// volume persistente é montado em <c>/data</c> — <c>WEBUI_DATA_DIR</c>
/// sobrepõe a raiz (o Dockerfile define <c>WEBUI_DATA_DIR=/data</c>).
/// </summary>
public static class AppData
{
    /// <summary>
    /// Raiz dos dados de runtime: <c>WEBUI_DATA_DIR</c> quando definida,
    /// senão <c>{ContentRootPath}/data</c> (comportamento de dev).
    /// </summary>
    public static string Root(IHostEnvironment env) =>
        Environment.GetEnvironmentVariable("WEBUI_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Join(env.ContentRootPath, "data");
}
