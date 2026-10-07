namespace OpenWebUI.Infrastructure.Terminal;

/// <summary>
/// Sessão de terminal interativo dentro de um PTY real — permite rodar o
/// shell do usuário com eco, cores e redimensionamento de janela.
/// </summary>
public interface IPtySession : IAsyncDisposable
{
    /// <summary>Disparado para cada chunk de saída do terminal (UTF-8, pode conter ANSI).</summary>
    event Action<string>? OutputReceived;

    /// <summary>Disparado uma vez quando o processo do shell encerra.</summary>
    event Action<int>? Exited;

    /// <summary>Última vez que input foi escrito — dirige o idle-timeout do manager.</summary>
    DateTimeOffset LastActivityUtc { get; }

    /// <summary>Se o processo subjacente ainda está rodando.</summary>
    bool IsRunning { get; }

    /// <summary>Inicia o PTY.</summary>
    void Start();

    /// <summary>Escreve input cru no stdin do shell (teclas, paste, control chars).</summary>
    Task WriteAsync(string data);

    /// <summary>Redimensiona a janela do PTY (SIGWINCH nos apps em primeiro plano).</summary>
    Task ResizeAsync(int cols, int rows);
}
