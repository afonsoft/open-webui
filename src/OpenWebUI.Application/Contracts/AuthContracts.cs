namespace OpenWebUI.Application.Contracts;

/// <summary>Papéis de usuário suportados, espelhando o Open WebUI.</summary>
public static class UserRoles
{
    /// <summary>Administrador com acesso total às configurações.</summary>
    public const string Admin = "admin";

    /// <summary>Usuário padrão da plataforma.</summary>
    public const string User = "user";

    /// <summary>Usuário pendente de aprovação.</summary>
    public const string Pending = "pending";
}

/// <summary>Requisição de cadastro de usuário.</summary>
/// <param name="Name">Nome de exibição.</param>
/// <param name="Email">E-mail único do usuário.</param>
/// <param name="Password">Senha em texto puro (trafegada somente via HTTPS).</param>
public sealed record SignUpRequest(string Name, string Email, string Password);

/// <summary>Requisição de autenticação.</summary>
/// <param name="Email">E-mail do usuário.</param>
/// <param name="Password">Senha do usuário.</param>
public sealed record SignInRequest(string Email, string Password);

/// <summary>Dados públicos de um usuário autenticado.</summary>
/// <param name="Id">Identificador do usuário.</param>
/// <param name="Name">Nome de exibição.</param>
/// <param name="Email">E-mail do usuário.</param>
/// <param name="Role">Papel (admin/user/pending).</param>
/// <param name="ProfileImageUrl">Imagem de perfil.</param>
/// <param name="Timezone">Fuso horário preferido.</param>
public sealed record UserResponse(
    string Id, string Name, string Email, string Role, string? ProfileImageUrl, string? Timezone);

/// <summary>Resposta de autenticação com token JWT.</summary>
/// <param name="Token">Token de acesso Bearer.</param>
/// <param name="TokenType">Tipo do token (sempre "Bearer").</param>
/// <param name="ExpiresAt">Instante de expiração em UTC.</param>
/// <param name="User">Dados do usuário autenticados.</param>
public sealed record AuthResponse(string Token, string TokenType, DateTimeOffset ExpiresAt, UserResponse User);

/// <summary>Atualização de perfil do usuário.</summary>
/// <param name="Name">Novo nome de exibição.</param>
/// <param name="ProfileImageUrl">URL ou data-url da imagem de perfil.</param>
public sealed record UpdateProfileRequest(string Name, string? ProfileImageUrl);

/// <summary>Troca de senha do usuário autenticado.</summary>
/// <param name="Password">Senha atual.</param>
/// <param name="NewPassword">Nova senha.</param>
public sealed record UpdatePasswordRequest(string Password, string NewPassword);

/// <summary>Atualização de fuso horário do usuário.</summary>
/// <param name="Timezone">Identificador IANA do fuso (ex.: "America/Sao_Paulo").</param>
public sealed record UpdateTimezoneRequest(string? Timezone);

/// <summary>Chave de API recém-criada (o valor só é retornado uma vez).</summary>
/// <param name="ApiKey">Chave em texto puro, prefixada por "sk-".</param>
public sealed record ApiKeyCreatedResponse(string ApiKey);

/// <summary>Metadados da chave de API ativa (sem o segredo).</summary>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
/// <param name="UpdatedAt">Última atualização (epoch seconds).</param>
public sealed record ApiKeyInfoResponse(long CreatedAt, long UpdatedAt);

/// <summary>Cadastro de usuário realizado por um admin.</summary>
/// <param name="Name">Nome de exibição.</param>
/// <param name="Email">E-mail do usuário.</param>
/// <param name="Password">Senha inicial.</param>
/// <param name="Role">Papel inicial (admin/user/pending).</param>
public sealed record AddUserRequest(string Name, string Email, string Password, string Role);

/// <summary>Configurações administrativas de autenticação/UI.</summary>
/// <param name="EnableSignup">Se o cadastro de novos usuários está aberto.</param>
/// <param name="EnableLoginForm">Se o formulário de login está habilitado.</param>
/// <param name="EnableApiKeys">Se usuários podem gerar chaves de API.</param>
/// <param name="DefaultUserRole">Papel atribuído a novos cadastros (pending/user).</param>
/// <param name="EnableMessageRating">Se avaliação de respostas (joinha) está habilitada.</param>
/// <param name="EnableFolders">Se organização de chats em pastas está habilitada.</param>
/// <param name="EnableMemories">Se memórias persistentes estão habilitadas.</param>
/// <param name="WebUiName">Nome exibido da instância.</param>
public sealed record AdminConfig(
    bool EnableSignup,
    bool EnableLoginForm,
    bool EnableApiKeys,
    string DefaultUserRole,
    bool EnableMessageRating,
    bool EnableFolders,
    bool EnableMemories,
    string WebUiName)
{
    /// <summary>Valores padrão compatíveis com o Open WebUI.</summary>
    public static AdminConfig Default { get; } = new(
        EnableSignup: true,
        EnableLoginForm: true,
        EnableApiKeys: true,
        DefaultUserRole: "pending",
        EnableMessageRating: true,
        EnableFolders: true,
        EnableMemories: false,
        WebUiName: "Open WebUI");
}

/// <summary>Usuário em listagens administrativas.</summary>
/// <param name="Id">Identificador do usuário.</param>
/// <param name="Name">Nome de exibição.</param>
/// <param name="Email">E-mail do usuário.</param>
/// <param name="Role">Papel atual.</param>
/// <param name="ProfileImageUrl">Imagem de perfil.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
/// <param name="LastActiveAt">Última atividade registrada (epoch seconds).</param>
public sealed record AdminUserResponse(
    string Id,
    string Name,
    string Email,
    string Role,
    string? ProfileImageUrl,
    long CreatedAt,
    long LastActiveAt);

/// <summary>Atualização administrativa de um usuário.</summary>
/// <param name="Name">Novo nome (opcional).</param>
/// <param name="Role">Novo papel (opcional).</param>
/// <param name="Password">Nova senha (opcional).</param>
/// <param name="ProfileImageUrl">Nova imagem de perfil (opcional).</param>
public sealed record AdminUpdateUserRequest(
    string? Name, string? Role, string? Password, string? ProfileImageUrl);

/// <summary>Configurações de UI persistidas por usuário.</summary>
/// <param name="SettingsJson">Objeto JSON arbitrário com preferências da interface.</param>
public sealed record UserSettingsRequest(string SettingsJson);
