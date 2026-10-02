namespace OpenWebUI.Application.Contracts;

/// <summary>Usuário SCIM 2.0 (RFC 7643, subconjunto core).</summary>
public sealed record ScimUser(
    List<string> Schemas,
    string Id,
    string UserName,
    string? DisplayName,
    ScimName? Name,
    List<ScimEmail>? Emails,
    bool Active,
    ScimMeta Meta);

/// <summary>Nome estruturado SCIM.</summary>
public sealed record ScimName(string? Formatted, string? GivenName, string? FamilyName);

/// <summary>E-mail SCIM.</summary>
public sealed record ScimEmail(string Value, string? Type, bool? Primary);

/// <summary>Meta de recurso SCIM.</summary>
public sealed record ScimMeta(string ResourceType, string? Location, string? Created, string? LastModified);

/// <summary>Resposta de listagem SCIM (RFC 7644 §3.4.2).</summary>
public sealed record ScimListResponse<T>(
    List<string> Schemas,
    int TotalResults,
    int StartIndex,
    int ItemsPerPage,
    List<T> Resources);

/// <summary>Grupo SCIM 2.0.</summary>
public sealed record ScimGroup(
    List<string> Schemas,
    string Id,
    string DisplayName,
    List<ScimGroupMember>? Members,
    ScimMeta Meta);

/// <summary>Membro de grupo SCIM.</summary>
public sealed record ScimGroupMember(string Value, string? Display);

/// <summary>Erro SCIM (RFC 7644 §3.12).</summary>
public sealed record ScimError(List<string> Schemas, string Status, string? ScimType, string? Detail)
{
    /// <summary>Cria um erro SCIM com o schema padrão.</summary>
    public static ScimError Of(int status, string detail, string? scimType = null) =>
        new(["urn:ietf:params:scim:api:messages:2.0:Error"], status.ToString(), scimType, detail);
}

/// <summary>Configuração SCIM exposta/aceita pelo admin (token mascarado).</summary>
public sealed record ScimConfigResponse(bool Enabled, bool HasToken);

/// <summary>Configuração SCIM do admin; token "********" preserva o atual.</summary>
public sealed record ScimConfigRequest(bool Enabled, string? Token);

/// <summary>Configuração SAML exposta/aceita pelo admin.</summary>
public sealed record SamlConfigResponse(
    bool Enabled,
    string IdpSsoUrl,
    string IdpEntityId,
    bool HasCert,
    string SpEntityId,
    string RoleAttribute);

/// <summary>Configuração SAML do admin (cert em PEM; "********" preserva).</summary>
public sealed record SamlConfigRequest(
    bool Enabled,
    string? IdpSsoUrl,
    string? IdpEntityId,
    string? IdpCert,
    string? SpEntityId,
    string? RoleAttribute);
