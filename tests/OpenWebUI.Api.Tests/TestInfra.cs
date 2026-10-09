using System.Collections;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Helpers compartilhados de infraestrutura dos testes.
/// </summary>
internal static class TestInfra
{
    /// <summary>Caminho novo para um SQLite temporário de teste.</summary>
    public static string NewDbPath(string prefix) =>
        Path.Join(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.db");

    /// <summary>
    /// Remove o arquivo de banco junto com os sidecars do modo WAL
    /// (<c>-wal</c>/<c>-shm</c>). O migrator liga <c>PRAGMA journal_mode=WAL</c>;
    /// apagar só o <c>.db</c> deixa o WAL para trás e, se outro teste recriar o
    /// arquivo no mesmo caminho (ex.: env <c>ConnectionStrings__Default</c>
    /// herdada de um fixture anterior), o SQLite recupera frames obsoletos e o
    /// banco "novo" nasce com um schema parcial — a migração então falha com
    /// <c>table "X" already exists</c>.
    /// </summary>
    public static void DeleteDb(string path)
    {
        File.Delete(path);
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
    }

    /// <summary>
    /// Fixa <c>ConnectionStrings__Default</c> num SQLite temporário novo por
    /// escopo: restaura o valor anterior e apaga o arquivo (com sidecars) no
    /// <see cref="IDisposable.Dispose"/>. Use em testes que criam
    /// <c>WebApplicationFactory</c> fora de um fixture com banco próprio —
    /// sem isso a factory herda a env var deixada por outro fixture.
    /// </summary>
    public static IDisposable UseDb(string prefix = "openwebui")
    {
        var path = NewDbPath(prefix);
        var previous = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={path}");
        return new DbScope(previous, path);
    }

    private sealed class DbScope(string? previous, string path) : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", previous);
            DeleteDb(path);
        }
    }
}

/// <summary>
/// Isola variáveis de ambiente por fixture: salva o ambiente antes do
/// <c>OneTimeSetUp</c> e o restaura depois do <c>OneTimeTearDown</c>.
/// Variáveis de ambiente são estado global do processo — um fixture que seta
/// <c>ConnectionStrings__Default</c> (ou flags como <c>GITHUB_CLIENT_ID</c>)
/// sem restaurar contaminaria os fixtures seguintes. Aplicado a cada
/// <see cref="TestFixtureAttribute"/> da suíte.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class IsolateEnvironmentAttribute : Attribute, ITestAction
{
    private Dictionary<string, string?>? _saved;

    public ActionTargets Targets => ActionTargets.Suite;

    public void BeforeTest(ITest test) => _saved = Snapshot();

    public void AfterTest(ITest test)
    {
        if (_saved is not null)
        {
            Restore(_saved);
        }
    }

    private static Dictionary<string, string?> Snapshot()
    {
        var snapshot = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            snapshot[(string)entry.Key] = (string?)entry.Value;
        }
        return snapshot;
    }

    private static void Restore(Dictionary<string, string?> saved)
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (!saved.ContainsKey(key))
            {
                Environment.SetEnvironmentVariable(key, null);
            }
        }

        foreach (var (key, value) in saved)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(key), value, StringComparison.Ordinal))
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
