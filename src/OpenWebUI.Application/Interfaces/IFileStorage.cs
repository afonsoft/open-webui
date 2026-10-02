namespace OpenWebUI.Application.Interfaces;

/// <summary>
/// Abstração do storage de arquivos (uploads, imagens geradas).
/// Implementações: <c>local</c> (disco — default) e <c>s3</c> (AWS/MinIO).
/// O <c>storagePath</c> retornado por <see cref="SaveAsync"/> é a chave
/// opaca persistida em <c>FileEntry.StoragePath</c> — cada implementação
/// decide seu formato (caminho absoluto local ou URI <c>s3://</c>).
/// </summary>
public interface IFileStorage
{
    /// <summary>Nome do provedor (<c>local</c>, <c>s3</c>) — usado no healthcheck.</summary>
    string Provider { get; }

    /// <summary>Grava o conteúdo e retorna a chave de storage.</summary>
    Task<string> SaveAsync(
        string userId, string fileId, string filename, Stream content,
        CancellationToken ct = default);

    /// <summary>Abre stream de leitura da chave. Lança quando inexistente.</summary>
    Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct = default);

    /// <summary>Remove a chave (idempotente — ignora inexistente).</summary>
    Task DeleteAsync(string storagePath, CancellationToken ct = default);

    /// <summary>Verifica disponibilidade do backend (healthcheck).</summary>
    Task<bool> PingAsync(CancellationToken ct = default);
}
