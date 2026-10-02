namespace OpenWebUI.Domain;

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

    /// <summary>Permissões próprias do usuário (JSON no formato de permissões granulares);
    /// recebe os defaults no signup e faz união com as permissões dos grupos.</summary>
    public string PermissionsJson { get; set; } = "{}";

    /// <summary>Última atividade registrada (epoch seconds; atualizado no signin e na conexão do hub).</summary>
    public long? LastActiveAt { get; set; }

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

    /// <summary>Ids das tools habilitadas neste chat, serializados como JSON.</summary>
    public string ToolIdsJson { get; set; } = "[]";

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

/// <summary>Grupo de usuários com permissões granulares (RBAC).</summary>
public class Group
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Nome do grupo.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Descrição opcional.</summary>
    public string? Description { get; set; }

    /// <summary>Flags de permissão serializadas como JSON (workspace/sharing/chat).</summary>
    public string PermissionsJson { get; set; } = "{}";

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }

    /// <summary>Membros do grupo.</summary>
    public List<GroupMember> Members { get; set; } = [];
}

/// <summary>Vínculo usuário ↔ grupo, com papel dentro do grupo.</summary>
public class GroupMember
{
    /// <summary>Grupo do vínculo.</summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>Grupo navegação.</summary>
    public Group? Group { get; set; }

    /// <summary>Usuário do vínculo.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Usuário navegação.</summary>
    public User? User { get; set; }

    /// <summary>Papel no grupo: "admin" (gerencia membros) ou "member".</summary>
    public string Role { get; set; } = "member";

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }
}

/// <summary>Conta externa OAuth/OIDC vinculada a um usuário local.</summary>
public class OAuthAccount
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Usuário local dono da conta.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Usuário navegação.</summary>
    public User? User { get; set; }

    /// <summary>Provedor (google, github, microsoft, oidc).</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Identificador da conta no provedor (sub/id).</summary>
    public string ProviderAccountId { get; set; } = string.Empty;

    /// <summary>E-mail reportado pelo provedor.</summary>
    public string? Email { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }
}

/// <summary>Coleção nomeada de arquivos para contexto RAG (aba Knowledge).</summary>
public class KnowledgeCollection
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da coleção.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Usuário navegação.</summary>
    public User? User { get; set; }

    /// <summary>Nome único por usuário.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Descrição opcional.</summary>
    public string? Description { get; set; }

    /// <summary>Arquivos vinculados.</summary>
    public List<KnowledgeFile> Files { get; set; } = [];

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Vínculo arquivo↔coleção de knowledge.</summary>
public class KnowledgeFile
{
    /// <summary>Identificador único do vínculo (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Coleção dona.</summary>
    public string CollectionId { get; set; } = string.Empty;

    /// <summary>Coleção navegação.</summary>
    public KnowledgeCollection? Collection { get; set; }

    /// <summary>Arquivo vinculado (FileEntry).</summary>
    public string FileId { get; set; } = string.Empty;

    /// <summary>Inclusão (epoch seconds).</summary>
    public long AddedAt { get; set; }
}

/// <summary>Trecho de texto indexado com embedding vetorial para RAG.</summary>
public class EmbeddingChunk
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Usuário dono do arquivo origem (isolamento por conta).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Arquivo origem (FileEntry).</summary>
    public string FileId { get; set; } = string.Empty;

    /// <summary>Ordem do chunk dentro do arquivo.</summary>
    public int ChunkIndex { get; set; }

    /// <summary>Texto do chunk.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Vetor de embedding serializado (JSON array de floats).</summary>
    public string EmbeddingJson { get; set; } = string.Empty;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }
}
/// <summary>Tool externa invocável pelo modelo (function calling via HTTP).</summary>
public class Tool
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da tool.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Usuário navegação.</summary>
    public User? User { get; set; }

    /// <summary>Nome de exibição.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Descrição exibida na UI.</summary>
    public string? Description { get; set; }

    /// <summary>Spec da função no formato OpenAI {"type":"function","function":{...}}.</summary>
    public string SpecJson { get; set; } = string.Empty;

    /// <summary>Endpoint HTTP POST que executa a tool (server-side, nunca exposto).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Se a tool está habilitada.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Canal de conversa em grupo (múltiplos usuários, estilo chat de equipe).</summary>
public class Channel
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Nome do canal.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Descrição opcional.</summary>
    public string? Description { get; set; }

    /// <summary>Tipo do canal: "channel" (grupo) — "dm" fica para fase futura.</summary>
    public string Type { get; set; } = "channel";

    /// <summary>Usuário criador (sempre admin do canal).</summary>
    public string CreatedByUserId { get; set; } = string.Empty;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }

    /// <summary>Membros do canal.</summary>
    public List<ChannelMember> Members { get; set; } = [];

    /// <summary>Mensagens do canal.</summary>
    public List<ChannelMessage> Messages { get; set; } = [];
}

/// <summary>Vínculo usuário↔canal com papel dentro do canal.</summary>
public class ChannelMember
{
    /// <summary>Canal.</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>Canal navegação.</summary>
    public Channel? Channel { get; set; }

    /// <summary>Usuário membro.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Usuário navegação.</summary>
    public User? User { get; set; }

    /// <summary>Papel no canal: "admin" (gerencia membros/canal) ou "member".</summary>
    public string Role { get; set; } = "member";

    /// <summary>Entrada (epoch seconds).</summary>
    public long CreatedAt { get; set; }
}

/// <summary>Mensagem de canal; autor é usuário ou modelo (UserId nulo = modelo).</summary>
public class ChannelMessage
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Canal dono da mensagem.</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>Canal navegação.</summary>
    public Channel? Channel { get; set; }

    /// <summary>Usuário autor; nulo quando a mensagem é do modelo (@menção).</summary>
    public string? UserId { get; set; }

    /// <summary>Usuário navegação.</summary>
    public User? User { get; set; }

    /// <summary>Identificador do modelo autor, quando UserId é nulo.</summary>
    public string? ModelId { get; set; }

    /// <summary>Conteúdo em texto/markdown.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }
}

/// <summary>Automação: prompt executado em um agendamento recorrente.</summary>
public class Automation
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da automação.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Navegação para o dono.</summary>
    public User? User { get; set; }

    /// <summary>Nome exibido na lista.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Prompt enviado ao modelo a cada execução.</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>Modelo usado na execução.</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Tipo de agenda: interval, daily ou weekly.</summary>
    public string ScheduleKind { get; set; } = "interval";

    /// <summary>Intervalo em minutos quando ScheduleKind = interval (mín. 1).</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>Hora do dia "HH:mm" (UTC) quando ScheduleKind = daily/weekly.</summary>
    public string? TimeOfDay { get; set; }

    /// <summary>Dia da semana 0-6 (domingo=0, UTC) quando ScheduleKind = weekly.</summary>
    public int? Weekday { get; set; }

    /// <summary>Indica se a automação está habilitada.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Próxima execução agendada (epoch seconds, UTC).</summary>
    public long? NextRunAt { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }

    /// <summary>Execuções registradas.</summary>
    public List<AutomationRun> Runs { get; set; } = [];
}

/// <summary>Execução registrada de uma automação.</summary>
public class AutomationRun
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Automação executada.</summary>
    public string AutomationId { get; set; } = string.Empty;

    /// <summary>Navegação para a automação.</summary>
    public Automation? Automation { get; set; }

    /// <summary>Resultado: ok ou failed.</summary>
    public string Status { get; set; } = "ok";

    /// <summary>Mensagem de erro quando Status = failed.</summary>
    public string? Error { get; set; }

    /// <summary>Chat criado com o prompt e a resposta, quando a execução completa.</summary>
    public string? ChatId { get; set; }

    /// <summary>Início da execução (epoch seconds).</summary>
    public long StartedAt { get; set; }

    /// <summary>Fim da execução (epoch seconds).</summary>
    public long FinishedAt { get; set; }
}

/// <summary>Banner de aviso exibido no topo do app (CRUD admin).</summary>
public class Banner
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Tipo visual: info, warning, error ou success.</summary>
    public string Type { get; set; } = "info";

    /// <summary>Título curto do aviso.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Conteúdo do aviso (texto).</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Se o usuário pode dispensar o banner.</summary>
    public bool Dismissible { get; set; } = true;

    /// <summary>Criação (epoch seconds; usado para ordenar).</summary>
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
