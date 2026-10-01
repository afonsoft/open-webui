namespace OpenWebUI.Server.Data;

/// <summary>Usuário da plataforma.</summary>
public class User
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Nome de exibição.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>E-mail único usado no login.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Hash da senha (PBKDF2 via ASP.NET Core PasswordHasher).</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Papel do usuário: admin, user ou pending.</summary>
    public string Role { get; set; } = "pending";

    /// <summary>URL ou data-url da imagem de perfil.</summary>
    public string? ProfileImageUrl { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }

    /// <summary>Chats do usuário.</summary>
    public List<Chat> Chats { get; set; } = [];
}

/// <summary>Conversa pertencente a um usuário.</summary>
public class Chat
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono do chat.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Navegação para o dono.</summary>
    public User? User { get; set; }

    /// <summary>Título exibido na barra lateral.</summary>
    public string Title { get; set; } = "New Chat";

    /// <summary>Modelos selecionados, serializados como JSON.</summary>
    public string ModelsJson { get; set; } = "[]";

    /// <summary>Indica se o chat está arquivado.</summary>
    public bool Archived { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }

    /// <summary>Mensagens em ordem cronológica.</summary>
    public List<ChatMessage> Messages { get; set; } = [];
}

/// <summary>Mensagem individual de um chat.</summary>
public class ChatMessage
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Chat ao qual a mensagem pertence.</summary>
    public string ChatId { get; set; } = string.Empty;

    /// <summary>Navegação para o chat.</summary>
    public Chat? Chat { get; set; }

    /// <summary>Papel: system, user ou assistant.</summary>
    public string Role { get; set; } = "user";

    /// <summary>Conteúdo textual em Markdown.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Modelo que produziu a mensagem (somente assistant).</summary>
    public string? Model { get; set; }

    /// <summary>Posição da mensagem dentro do chat.</summary>
    public int Position { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long Timestamp { get; set; }
}

/// <summary>Entrada chave-valor de configuração persistida (espelha a tabela config do Open WebUI).</summary>
public class ConfigEntry
{
    /// <summary>Chave da configuração.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Valor serializado em JSON.</summary>
    public string ValueJson { get; set; } = string.Empty;

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}
