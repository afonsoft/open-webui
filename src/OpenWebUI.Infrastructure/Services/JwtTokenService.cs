using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>Emite e valida tokens JWT de sessão.</summary>
public class JwtTokenService(ConfigService config)
{
    /// <summary>Duração padrão do token: 7 dias.</summary>
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Interpreta uma duração textual do JWT ("30m", "24h", "7d", "4w" ou segundos inteiros).
    /// Retorna <c>null</c> quando inválida ou não positiva.
    /// </summary>
    public static TimeSpan? ParseLifetime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().ToLowerInvariant();
        var suffix = trimmed[^1];
        var numberPart = char.IsLetter(suffix) ? trimmed[..^1] : trimmed;
        if (!double.TryParse(numberPart, out var amount) || amount <= 0)
        {
            return null;
        }

        return suffix switch
        {
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            'w' => TimeSpan.FromDays(amount * 7),
            _ when char.IsDigit(suffix) => TimeSpan.FromSeconds(amount),
            _ => null,
        };
    }

    /// <summary>Emite um token JWT para o usuário informado.</summary>
    /// <param name="user">Usuário autenticado.</param>
    /// <param name="ct">Token de cancelamento.</param>
    /// <returns>Token assinado e o instante de expiração.</returns>
    public async Task<(string Token, DateTimeOffset ExpiresAt)> CreateTokenAsync(
        User user, CancellationToken ct = default)
    {
        var secret = await config.GetOrCreateJwtSecretAsync(ct);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var configured = await config.GetAsync("webui.jwt.expires_in", "7d", ct);
        var lifetime = ParseLifetime(configured) ?? DefaultLifetime;
        var expires = DateTimeOffset.UtcNow.Add(lifetime);

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
