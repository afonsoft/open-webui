using Microsoft.EntityFrameworkCore;

namespace OpenWebUI.Server.Data;

/// <summary>Contexto EF Core do backend .NET (SQLite por padrão).</summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <summary>Usuários cadastrados.</summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>Chats persistidos.</summary>
    public DbSet<Chat> Chats => Set<Chat>();

    /// <summary>Mensagens de chats.</summary>
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    /// <summary>Configurações chave-valor.</summary>
    public DbSet<ConfigEntry> ConfigEntries => Set<ConfigEntry>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.HasIndex(u => u.Email).IsUnique();
            entity.HasMany(u => u.Chats)
                .WithOne(c => c.User!)
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Chat>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.UserId, c.UpdatedAt });
            entity.HasMany(c => c.Messages)
                .WithOne(m => m.Chat!)
                .HasForeignKey(m => m.ChatId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => new { m.ChatId, m.Position });
        });

        modelBuilder.Entity<ConfigEntry>(entity =>
        {
            entity.HasKey(e => e.Key);
        });
    }
}
