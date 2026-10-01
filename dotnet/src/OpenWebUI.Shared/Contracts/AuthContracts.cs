namespace OpenWebUI.Shared.Contracts;

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
public sealed record UserResponse(string Id, string Name, string Email, string Role);

/// <summary>Resposta de autenticação com token JWT.</summary>
/// <param name="Token">Token de acesso Bearer.</param>
/// <param name="TokenType">Tipo do token (sempre "Bearer").</param>
/// <param name="ExpiresAt">Instante de expiração em UTC.</param>
/// <param name="User">Dados do usuário autenticado.</param>
public sealed record AuthResponse(string Token, string TokenType, DateTimeOffset ExpiresAt, UserResponse User);

/// <summary>Atualização de perfil do usuário.</summary>
/// <param name="Name">Novo nome de exibição.</param>
/// <param name="ProfileImageUrl">URL ou data-url da imagem de perfil.</param>
public sealed record UpdateProfileRequest(string Name, string? ProfileImageUrl);
