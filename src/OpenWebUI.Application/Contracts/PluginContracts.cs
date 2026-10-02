namespace OpenWebUI.Application.Contracts;

/// <summary>Skill do workspace (resposta).</summary>
public sealed record SkillResponse(
    string Id, string Name, string? Description, string Content,
    bool IsActive, long CreatedAt, long UpdatedAt);

/// <summary>Criação/atualização de skill.</summary>
public sealed record SkillRequest(string Name, string? Description, string Content);

/// <summary>Function do ecossistema (resposta; valores de valves incluídos).</summary>
public sealed record FunctionResponse(
    string Id, string Name, string Type, string ManifestJson,
    string? ValvesJson, bool Active, long CreatedAt, long UpdatedAt);

/// <summary>Criação/atualização de function.</summary>
public sealed record FunctionRequest(
    string Name, string Type, string? ManifestJson, string? ValvesJson);

/// <summary>Atualização somente dos valves de uma function.</summary>
public sealed record FunctionValvesRequest(string ValvesJson);

/// <summary>Servidor de pipelines (resposta; key nunca exposta).</summary>
public sealed record PipelineServerResponse(
    string Id, string Name, string Url, bool HasKey, long CreatedAt);

/// <summary>Registro/atualização de servidor de pipelines; key "********" preserva.</summary>
public sealed record PipelineServerRequest(string Name, string Url, string? Key);

/// <summary>Pipe descoberto num servidor de pipelines (exposto como `pipeline:{id}`).</summary>
public sealed record PipelinePipeResponse(string Id, string Name, string ServerId);
