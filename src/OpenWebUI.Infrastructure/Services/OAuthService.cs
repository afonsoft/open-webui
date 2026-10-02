using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Vincula identidades OAuth/OIDC a usuários locais: conta existente retorna o
/// mesmo usuário; e-mail já cadastrado vincula; e-mail novo cria usuário com o
/// papel padrão configurado (sem senha local — login só via provedor).
/// </summary>
public class OAuthService(AppDbContext db, ConfigService config)
{
    /// <summary>Vincula ou cria um usuário a partir dos dados do provedor.</summary>
    /// <param name="provider">Slug do provedor (google, github, microsoft, oidc).</param>
    /// <param name="subject">Identificador da conta no provedor.</param>
    /// <param name="email">E-mail reportado pelo provedor.</param>
    /// <param name="name">Nome de exibição (opcional).</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task<OAuthLinkResult> LinkOrCreateAsync(
        string provider, string subject, string email, string? name,
        CancellationToken ct = default)
    {
        provider = provider.Trim().ToLowerInvariant();
        email = email.Trim().ToLowerInvariant();

        var existing = await db.OAuthAccounts
            .FirstOrDefaultAsync(
                a => a.Provider == provider && a.ProviderAccountId == subject, ct);
        if (existing is not null)
        {
            return new OAuthLinkResult(existing.UserId, NewUser: false);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        var newUser = false;
        if (user is null)
        {
            var adminConfig = await config.GetAdminConfigAsync(ct);
            var anyUser = await db.Users.AnyAsync(ct);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            user = new User
            {
                Name = string.IsNullOrWhiteSpace(name) ? email : name.Trim(),
                Email = email,
                Role = !anyUser ? UserRoles.Admin : NormalizeRole(adminConfig.DefaultUserRole),
                PermissionsJson = "{}",
                PasswordHash = string.Empty, // login somente via provedor
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Users.Add(user);
            newUser = true;
        }

        db.OAuthAccounts.Add(new OAuthAccount
        {
            UserId = user.Id,
            Provider = provider,
            ProviderAccountId = subject,
            Email = email,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        await db.SaveChangesAsync(ct);

        return new OAuthLinkResult(user.Id, newUser);
    }

    private static string NormalizeRole(string? role) =>
        role is UserRoles.Admin or UserRoles.User ? role : UserRoles.Pending;
}
