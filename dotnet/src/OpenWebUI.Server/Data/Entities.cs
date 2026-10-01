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

    /// <summary>Fuso horário preferido (ex.: "America/Sao_Paulo").</summary>
    public string? Timezone { get; set; }

    /// <summary>Configurações de UI do usuário, serializadas como JSON.</summary>
    public string SettingsJson { get; set; } = "{}";

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }

    /// <summary>Chats do usuário.</summary>
    public List<Chat> Chats { get; set; } = [];
}

/// <summary>Chave de API gerada por um usuário (prefixo sk-).</summary>
public class ApiKey
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da chave.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Hash SHA-256 da chave de API (a chave em si só é exibida na criação).</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
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

    /// <summary>Tags do chat, serializadas como JSON.</summary>
    public string TagsJson { get; set; } = "[]";

    /// <summary>Indica se o chat está arquivado.</summary>
    public bool Archived { get; set; }

    /// <summary>Indica se o chat está fixado no topo da lista.</summary>
    public bool Pinned { get; set; }

    /// <summary>Id da pasta que contém o chat, quando organizado em pastas.</summary>
    public string? FolderId { get; set; }

    /// <summary>Id público de compartilhamento (rota /s/{shareId}), quando compartilhado.</summary>
    public string? ShareId { get; set; }

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

/// <summary>Pasta para organizar chats na barra lateral.</summary>
public class Folder
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da pasta.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Nome exibido.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Pasta-pai, para pastas aninhadas (opcional).</summary>
    public string? ParentId { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Prompt personalizado do workspace (acionado por /comando).</summary>
public class Prompt
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono do prompt.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Comando que aciona o prompt (ex.: "resumir" → /resumir).</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Título exibido.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Texto do prompt com placeholders {{CLIPBOARD}}, {{USER:name}} etc.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Arquivo enviado pelo usuário para uso como contexto em chats.</summary>
public class FileEntry
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono do arquivo.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Nome original do arquivo.</summary>
    public string Filename { get; set; } = string.Empty;

    /// <summary>Tipo MIME reportado no upload.</summary>
    public string? ContentType { get; set; }

    /// <summary>Caminho no disco onde o binário foi gravado.</summary>
    public string StoragePath { get; set; } = string.Empty;

    /// <summary>Tamanho em bytes.</summary>
    public long Size { get; set; }

    /// <summary>Texto extraído do arquivo (somente formatos de texto suportados).</summary>
    public string? ExtractedText { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Modelo personalizado do workspace: camada sobre um modelo base com system prompt e parâmetros.</summary>
public class ModelEntry
{
    /// <summary>Identificador único (GUID interno).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono do modelo personalizado.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Nome público exibido no seletor de modelos.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Modelo base usado na completion (id do provedor).</summary>
    public string BaseModelId { get; set; } = string.Empty;

    /// <summary>System prompt aplicado antes das mensagens do usuário.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>Parâmetros de geração (temperature, top_p, etc.), serializados como JSON.</summary>
    public string? ParamsJson { get; set; }

    /// <summary>URL ou data-url da imagem do modelo.</summary>
    public string? ProfileImageUrl { get; set; }

    /// <summary>Sugestões de prompt exibidas em chat vazio, serializadas como JSON.</summary>
    public string? SuggestionPromptsJson { get; set; }

    /// <summary>Indica se o modelo está ativo/visível.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Memória persistente do usuário injetada no contexto dos chats.</summary>
public class MemoryEntry
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da memória.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Conteúdo lembrado (fato, preferência, contexto).</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Nota do usuário com conteúdo em Markdown.</summary>
public class Note
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da nota.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Título da nota.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Conteúdo em Markdown.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Avaliação (joinha) registrada em uma mensagem de assistant.</summary>
public class Feedback
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Usuário que avaliou.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Chat que contém a mensagem avaliada.</summary>
    public string ChatId { get; set; } = string.Empty;

    /// <summary>Mensagem avaliada.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>Modelo que produziu a mensagem.</summary>
    public string? ModelId { get; set; }

    /// <summary>Avaliação: 1 (positiva) ou -1 (negativa).</summary>
    public int Rating { get; set; }

    /// <summary>Motivo opcional da avaliação.</summary>
    public string? Reason { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}
