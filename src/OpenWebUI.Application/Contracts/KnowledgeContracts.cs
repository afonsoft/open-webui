namespace OpenWebUI.Application.Contracts;

/// <summary>Coleção de knowledge na listagem.</summary>
/// <param name="Id">Identificador.</param>
/// <param name="Name">Nome.</param>
/// <param name="Description">Descrição opcional.</param>
/// <param name="FileCount">Arquivos vinculados.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
public sealed record KnowledgeResponse(
    string Id,
    string Name,
    string? Description,
    int FileCount,
    long CreatedAt);

/// <summary>Coleção com seus arquivos.</summary>
/// <param name="Files">Arquivos vinculados.</param>
public sealed record KnowledgeDetailResponse(
    string Id,
    string Name,
    string? Description,
    long CreatedAt,
    IReadOnlyList<KnowledgeFileResponse> Files);

/// <summary>Arquivo de uma coleção.</summary>
/// <param name="Id">Id do vínculo.</param>
/// <param name="FileId">Id do arquivo.</param>
/// <param name="Filename">Nome do arquivo.</param>
/// <param name="AddedAt">Inclusão (epoch seconds).</param>
public sealed record KnowledgeFileResponse(
    string Id,
    string FileId,
    string Filename,
    long AddedAt);

/// <summary>Criação de coleção.</summary>
/// <param name="Name">Nome único por usuário.</param>
/// <param name="Description">Descrição opcional.</param>
public sealed record CreateKnowledgeRequest(string Name, string? Description);

/// <summary>Atualização de coleção.</summary>
/// <param name="Name">Novo nome (opcional).</param>
/// <param name="Description">Nova descrição (opcional).</param>
public sealed record UpdateKnowledgeRequest(string? Name, string? Description);

/// <summary>Vínculo de arquivo a uma coleção.</summary>
/// <param name="FileId">Arquivo já enviado via /api/v1/files.</param>
public sealed record AddKnowledgeFileRequest(string FileId);

/// <summary>Operação em lote sobre coleções de knowledge.</summary>
/// <param name="Ids">Ids das coleções alvo.</param>
public sealed record BatchKnowledgeRequest(IReadOnlyList<string> Ids);

/// <summary>Resultado de uma reindexação de coleção.</summary>
/// <param name="Status">True quando todos os arquivos reindexaram.</param>
/// <param name="Indexed">Arquivos reindexados com sucesso.</param>
/// <param name="Failed">Arquivos que falharam (provider de embedding).</param>
public sealed record ReindexKnowledgeResponse(bool Status, int Indexed, int Failed);
