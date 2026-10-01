using Microsoft.EntityFrameworkCore;

namespace OpenWebUI.Server.Data;

/// <summary>Contexto EF Core do backend .NET (SQLite por padrão).</summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <summary>Usuários cadastrados.</summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>Chaves de API geradas por usuários.</summary>
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    /// <summary>Chats persistidos.</summary>
    public DbSet<Chat> Chats => Set<Chat>();

    /// <summary>Mensagens de chats.</summary>
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    /// <summary>Pastas de organização de chats.</summary>
    public DbSet<Folder> Folders => Set<Folder>();

    /// <summary>Prompts personalizados do workspace.</summary>
    public DbSet<Prompt> Prompts => Set<Prompt>();

    /// <summary>Arquivos enviados pelos usuários.</summary>
    public DbSet<FileEntry> Files => Set<FileEntry>();

    /// <summary>Modelos personalizados do workspace.</summary>
    public DbSet<ModelEntry> ModelEntries => Set<ModelEntry>();

    /// <summary>Memórias persistentes dos usuários.</summary>
    public DbSet<MemoryEntry> Memories => Set<MemoryEntry>();

    /// <summary>Notas dos usuários.</summary>
    public DbSet<Note> Notes => Set<Note>();

    /// <summary>Avaliações de mensagens.</summary>
    public DbSet<Feedback> Feedbacks => Set<Feedback>();

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

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.HasKey(k => k.Id);
            entity.HasIndex(k => k.KeyHash).IsUnique();
            entity.HasIndex(k => k.UserId);
        });

        modelBuilder.Entity<Chat>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.UserId, c.UpdatedAt });
            entity.HasIndex(c => c.ShareId).IsUnique();
            entity.HasIndex(c => new { c.UserId, c.FolderId });
            entity.HasMany(c => c.Messages)
                .WithOne(m => m.Chat!)
                .HasForeignKey(m => m.ChatId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatMessage>(entity =>
        {
            // Ids de mensagem são únicos dentro do chat (o original usa UUIDs locais).
            entity.HasKey(m => new { m.ChatId, m.Id });
            entity.HasIndex(m => new { m.ChatId, m.Position });
        });

        modelBuilder.Entity<Folder>(entity =>
        {
            entity.HasKey(f => f.Id);
            entity.HasIndex(f => f.UserId);
        });

        modelBuilder.Entity<Prompt>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.UserId, p.Command }).IsUnique();
        });

        modelBuilder.Entity<FileEntry>(entity =>
        {
            entity.HasKey(f => f.Id);
            entity.HasIndex(f => f.UserId);
        });

        modelBuilder.Entity<ModelEntry>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => m.UserId);
        });

        modelBuilder.Entity<MemoryEntry>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => m.UserId);
        });

        modelBuilder.Entity<Note>(entity =>
        {
            entity.HasKey(n => n.Id);
            entity.HasIndex(n => n.UserId);
        });

        modelBuilder.Entity<Feedback>(entity =>
        {
            entity.HasKey(f => f.Id);
            entity.HasIndex(f => new { f.UserId, f.MessageId }).IsUnique();
        });

        modelBuilder.Entity<ConfigEntry>(entity =>
        {
            entity.HasKey(e => e.Key);
        });
    }
}
