namespace OpenWebUI.Application.Contracts;

/// <summary>Servidor de terminal/Jupyter configurado pelo admin (persistido em config).</summary>
/// <param name="Id">Identificador gerado.</param>
/// <param name="Name">Nome de exibição.</param>
/// <param name="Url">URL base do servidor (ex.: http://jupyter:8888).</param>
/// <param name="AuthType">none | token | password.</param>
/// <param name="Key">Token/senha — nunca serializado para o cliente.</param>
/// <param name="Type">jupyter | pty.</param>
public sealed record TerminalServerConfig(
    string Id, string Name, string Url, string AuthType, string Key, string Type);

/// <summary>Criação/atualização de um terminal server (admin).</summary>
public sealed record TerminalServerRequest(
    string Name, string Url, string AuthType = "token", string? Key = null, string Type = "jupyter");

/// <summary>Terminal server exposto ao cliente (sem a key).</summary>
public sealed record TerminalServerResponse(
    string Id, string Name, string Url, string AuthType, string Type, bool HasKey);
