using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using OpenWebUI.Server.Data;

namespace OpenWebUI.Server.Services;

/// <summary>Emite e valida tokens JWT de sessão.</summary>
public class JwtTokenService(ConfigService config)
{
    /// <summary>Duração padrão do token: 7 dias.</summary>
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(7);

    /// <summary>Emite um token JWT para o usuário informado.</summary>
    /// <param name="user">Usuário autenticado.</param>
    /// <param name="ct">Token de cancelamento.</param>
    /// <returns>Token assinado e o instante de expiração.</returns>
    public async Task<(string Token, DateTimeOffset ExpiresAt)> CreateTokenAsync(
        User user, CancellationToken ct = default)
    {
        var secret = await config.GetOrCreateJwtSecretAsync(ct);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var expires = DateTimeOffset.UtcNow.Add(TokenLifetime);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role),
        };

        var token = new JwtSecurityToken(
            claims: claims,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    /// <summary>Constrói os parâmetros de validação usados pelo middleware de autenticação.</summary>
    /// <param name="secret">Segredo de assinatura.</param>
    public static TokenValidationParameters BuildValidationParameters(string secret) => new()
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
        ClockSkew = TimeSpan.FromMinutes(1),
    };
}
