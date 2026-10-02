namespace OpenWebUI.Application.Contracts;

/// <summary>Tool registrada na listagem/edição.</summary>
/// <param name="Id">Identificador.</param>
/// <param name="Name">Nome.</param>
/// <param name="Description">Descrição.</param>
/// <param name="SpecJson">Spec da função no formato OpenAI.</param>
/// <param name="Code">Fonte Python da tool (class Tools), quando definida.</param>
/// <param name="Enabled">Se está habilitada.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
public sealed record ToolResponse(
    string Id,
    string Name,
    string? Description,
    string SpecJson,
    string? Code,
    bool Enabled,
    long CreatedAt);

/// <summary>Criação/atualização de tool.</summary>
/// <param name="Name">Nome.</param>
/// <param name="Description">Descrição.</param>
/// <param name="SpecJson">Spec da função {"type":"function","function":{...}}.</param>
/// <param name="Url">Endpoint HTTP POST de execução (obrigatório na criação quando Code ausente; omitir para manter).</param>
/// <param name="Code">Fonte Python executada em subprocess (convenção <c>class Tools</c> do upstream). Alternativa ao Url.</param>
/// <param name="Enabled">Habilitada.</param>
public sealed record ToolUpsertRequest(
    string Name,
    string? Description,
    string SpecJson,
    string? Url,
    string? Code = null,
    bool Enabled = true);
