namespace OpenWebUI.Infrastructure.Data;

/// <summary>Providers de banco suportados — seleção via env/config.</summary>
public enum DatabaseProviders
{
    /// <summary>SQLite embutido (default, single-instance).</summary>
    Sqlite,

    /// <summary>PostgreSQL via Npgsql (multi-instance).</summary>
    Postgresql,
}

/// <summary>Parsing da seleção de provider.</summary>
public static class DatabaseProviderSelector
{
    /// <summary>Resolve <c>DATABASE_PROVIDER</c>/<c>DatabaseProvider</c>;
    /// qualquer valor não reconhecido cai em sqlite (comportamento anterior).</summary>
    public static DatabaseProviders Resolve(string? value) =>
        string.Equals(value, "postgresql", StringComparison.OrdinalIgnoreCase)
            ? DatabaseProviders.Postgresql
            : DatabaseProviders.Sqlite;
}
