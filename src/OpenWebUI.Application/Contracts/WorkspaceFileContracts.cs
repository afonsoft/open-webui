namespace OpenWebUI.Application.Contracts;

/// <summary>Contratos da Workspace File API (SPEC-20261009-workspace-file-api, E16 S1).</summary>
/// <param name="Name">Nome da entrada (basename).</param>
/// <param name="Path">Caminho relativo ao workdir, separador '/'.</param>
/// <param name="Type">"file" ou "dir".</param>
/// <param name="Size">Tamanho em bytes (somente arquivos).</param>
/// <param name="Collapsed">Dir volumoso/gerado (.git, bin, obj, node_modules) — sem filhos por padrão.</param>
public sealed record WorkspaceFileEntryResponse(
    string Name, string Path, string Type, long? Size, bool Collapsed);

/// <summary>Resposta paginada da árvore do workdir.</summary>
/// <param name="Path">Prefixo da listagem (relativo).</param>
/// <param name="Entries">Entradas em DFS, dirs primeiro por nível.</param>
/// <param name="NextCursor">Cursor opaco para a próxima página (null = fim).</param>
/// <param name="Truncated">True quando há mais entradas além da página.</param>
public sealed record WorkspaceFileTreeResponse(
    string Path, IReadOnlyList<WorkspaceFileEntryResponse> Entries, string? NextCursor, bool Truncated);

/// <summary>Leitura paginada de arquivo texto.</summary>
/// <param name="Path">Caminho relativo.</param>
/// <param name="Content">Fatia de linhas pedida.</param>
/// <param name="TotalLines">Linhas totais do arquivo.</param>
/// <param name="Truncated">True quando há mais linhas além da fatia.</param>
/// <param name="ETag">Identidade de conteúdo (mtime+size) para If-Match/cache.</param>
public sealed record WorkspaceFileReadResponse(
    string Path, string Content, int TotalLines, bool Truncated, string ETag);

/// <summary>Escrita de arquivo texto (cria ou sobrescreve).</summary>
public sealed record WorkspaceFileWriteRequest(string? Path, string? Content);

/// <summary>Resposta de escrita com o novo etag.</summary>
public sealed record WorkspaceFileWriteResponse(
    bool Ok, string Path, string ETag, DateTimeOffset LastModified);

/// <summary>Criação de diretório (idempotente).</summary>
public sealed record WorkspaceFileMkdirRequest(string? Path);

/// <summary>Renomeação/movimentação de arquivo ou diretório.</summary>
public sealed record WorkspaceFileRenameRequest(string? From, string? To);

/// <summary>Remoção de arquivo ou diretório (recursiva).</summary>
public sealed record WorkspaceFileDeleteRequest(string? Path);
