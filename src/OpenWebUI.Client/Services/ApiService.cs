using System.Net.Http.Json;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Client.Services;

/// <summary>Cliente HTTP tipado para a API do backend .NET.</summary>
public class ApiService(HttpClient http, AuthService auth)
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    // ---------------- Chats ----------------

    /// <summary>Lista os chats do usuário, opcionalmente filtrados por texto.</summary>
    public async Task<List<ChatSummaryResponse>> GetChatsAsync(string? query = null, bool includeFolders = false)
    {
        var uri = "/api/v1/chats/?includeFolders=" + (includeFolders ? "true" : "false");
        if (!string.IsNullOrWhiteSpace(query))
        {
            uri += $"&query={Uri.EscapeDataString(query)}";
        }

        return await SendAsync<List<ChatSummaryResponse>>(HttpMethod.Get, uri) ?? [];
    }

    /// <summary>Lista chats arquivados.</summary>
    public async Task<List<ChatSummaryResponse>> GetArchivedChatsAsync() =>
        await SendAsync<List<ChatSummaryResponse>>(HttpMethod.Get, "/api/v1/chats/archived") ?? [];

    /// <summary>Lista chats fixados.</summary>
    public async Task<List<ChatSummaryResponse>> GetPinnedChatsAsync() =>
        await SendAsync<List<ChatSummaryResponse>>(HttpMethod.Get, "/api/v1/chats/pinned") ?? [];

    /// <summary>Obtém um chat completo com mensagens.</summary>
    public Task<ChatResponse?> GetChatAsync(string id) =>
        SendAsync<ChatResponse>(HttpMethod.Get, $"/api/v1/chats/{id}");

    /// <summary>Obtém um chat compartilhado publicamente (sem autenticação).</summary>
    public async Task<SharedChatResponse?> GetSharedChatAsync(string shareId)
    {
        using var response = await http.GetAsync($"/api/v1/chats/share/{shareId}");
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<SharedChatResponse>(JsonOptions)
            : null;
    }

    /// <summary>Cria um chat e retorna o recurso criado.</summary>
    public Task<ChatResponse?> CreateChatAsync(ChatUpsertRequest request) =>
        SendAsync<ChatResponse>(HttpMethod.Post, "/api/v1/chats/", request);

    /// <summary>Atualiza título, modelos e mensagens de um chat.</summary>
    public Task<ChatResponse?> UpdateChatAsync(string id, ChatUpsertRequest request) =>
        SendAsync<ChatResponse>(HttpMethod.Post, $"/api/v1/chats/{id}", request);

    /// <summary>Alterna o estado de fixado de um chat.</summary>
    public Task<ChatResponse?> TogglePinChatAsync(string id) =>
        SendAsync<ChatResponse>(HttpMethod.Post, $"/api/v1/chats/{id}/pin");

    /// <summary>Alterna o estado de arquivado de um chat.</summary>
    public Task<ChatResponse?> ToggleArchiveChatAsync(string id) =>
        SendAsync<ChatResponse>(HttpMethod.Post, $"/api/v1/chats/{id}/archive");

    /// <summary>Gera (ou reutiliza) o link público de compartilhamento do chat.</summary>
    public Task<ChatResponse?> ShareChatAsync(string id) =>
        SendAsync<ChatResponse>(HttpMethod.Post, $"/api/v1/chats/{id}/share");

    /// <summary>Remove o compartilhamento público do chat.</summary>
    public async Task<bool> UnshareChatAsync(string id) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/chats/{id}/share");

    /// <summary>Clona um chat inteiro.</summary>
    public Task<ChatResponse?> CloneChatAsync(string id) =>
        SendAsync<ChatResponse>(HttpMethod.Post, $"/api/v1/chats/{id}/clone");

    /// <summary>Move o chat para uma pasta (folderId vazio remove da pasta).</summary>
    public Task<ChatResponse?> SetChatFolderAsync(string id, string? folderId) =>
        SendAsync<ChatResponse>(HttpMethod.Post, $"/api/v1/chats/{id}/folder",
            new { folderId });

    /// <summary>Define as tags de um chat.</summary>
    public Task<ChatResponse?> SetChatTagsAsync(string id, IReadOnlyList<string> tags) =>
        SendAsync<ChatResponse>(HttpMethod.Post, $"/api/v1/chats/{id}/tags",
            new { tags });

    /// <summary>Atualiza o conteúdo de uma mensagem.</summary>
    public Task<ChatResponse?> UpdateMessageAsync(string chatId, string messageId, string content) =>
        SendAsync<ChatResponse>(HttpMethod.Post, $"/api/v1/chats/{chatId}/messages/{messageId}",
            new MessageUpdateRequest(content));

    /// <summary>Exclui uma mensagem e as seguintes.</summary>
    public Task<ChatResponse?> DeleteMessageAsync(string chatId, string messageId) =>
        SendAsync<ChatResponse>(HttpMethod.Delete, $"/api/v1/chats/{chatId}/messages/{messageId}");

    /// <summary>Remove um chat.</summary>
    public async Task<bool> DeleteChatAsync(string id)
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, $"/api/v1/chats/{id}");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Exporta todos os chats do usuário.</summary>
    public Task<object?> ExportChatsAsync() =>
        SendAsync<object>(HttpMethod.Get, "/api/v1/chats/all/db");

    // ---------------- Pastas ----------------

    /// <summary>Lista pastas do usuário com os chats de cada uma.</summary>
    public async Task<List<FolderWithChats>> GetFoldersAsync()
    {
        var raw = await SendAsync<List<System.Text.Json.JsonElement>>(
            HttpMethod.Get, "/api/v1/folders/");
        if (raw is null)
        {
            return [];
        }

        var result = new List<FolderWithChats>();
        foreach (var element in raw)
        {
            var folder = new FolderWithChats
            {
                Id = element.GetProperty("id").GetString() ?? string.Empty,
                Name = element.GetProperty("name").GetString() ?? string.Empty,
            };
            if (element.TryGetProperty("items", out var items)
                && items.TryGetProperty("chats", out var chats))
            {
                foreach (var chat in chats.EnumerateArray())
                {
                    folder.Chats.Add(new ChatSummaryResponse(
                        chat.GetProperty("id").GetString() ?? string.Empty,
                        chat.GetProperty("title").GetString() ?? string.Empty,
                        chat.TryGetProperty("pinned", out var p) && p.GetBoolean(),
                        chat.TryGetProperty("folderId", out var f) ? f.GetString() : null,
                        [],
                        chat.GetProperty("createdAt").GetInt64(),
                        chat.GetProperty("updatedAt").GetInt64()));
                }
            }

            result.Add(folder);
        }

        return result;
    }

    /// <summary>Cria uma pasta.</summary>
    public Task<FolderResponse?> CreateFolderAsync(string name) =>
        SendAsync<FolderResponse>(HttpMethod.Post, "/api/v1/folders/",
            new FolderUpsertRequest(name, null));

    /// <summary>Renomeia uma pasta.</summary>
    public Task<FolderResponse?> UpdateFolderAsync(string id, string name) =>
        SendAsync<FolderResponse>(HttpMethod.Post, $"/api/v1/folders/{id}/update",
            new FolderUpsertRequest(name, null));

    /// <summary>Remove uma pasta.</summary>
    public async Task<bool> DeleteFolderAsync(string id) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/folders/{id}");

    // ---------------- Modelos ----------------

    /// <summary>Lista modelos disponíveis em todos os provedores.</summary>
    public async Task<List<ModelInfo>> GetModelsAsync()
    {
        var result = await SendAsync<ModelListResponse>(HttpMethod.Get, "/api/models");
        return result?.Data.ToList() ?? [];
    }

    /// <summary>Lista modelos personalizados do workspace.</summary>
    public async Task<List<ModelEntryResponse>> GetCustomModelsAsync() =>
        await SendAsync<List<ModelEntryResponse>>(HttpMethod.Get, "/api/v1/models/") ?? [];

    /// <summary>Cria um modelo personalizado.</summary>
    public Task<ModelEntryResponse?> CreateCustomModelAsync(ModelEntryUpsertRequest request) =>
        SendAsync<ModelEntryResponse>(HttpMethod.Post, "/api/v1/models/create", request);

    /// <summary>Atualiza um modelo personalizado.</summary>
    public Task<ModelEntryResponse?> UpdateCustomModelAsync(string id, ModelEntryUpsertRequest request) =>
        SendAsync<ModelEntryResponse>(HttpMethod.Post, "/api/v1/models/model/update",
            new { id, request.Name, request.BaseModelId, request.SystemPrompt, request.ParamsJson, request.ProfileImageUrl });

    /// <summary>Remove um modelo personalizado.</summary>
    public async Task<bool> DeleteCustomModelAsync(string id) =>
        await SendStatusAsync(HttpMethod.Post, $"/api/v1/models/model/delete", new { id });

    /// <summary>Alterna visibilidade de um modelo personalizado.</summary>
    public Task<ModelEntryResponse?> ToggleCustomModelAsync(string id) =>
        SendAsync<ModelEntryResponse>(HttpMethod.Post, "/api/v1/models/model/toggle", new { id });

    // ---------------- Tools ----------------

    /// <summary>Lista tools do usuário.</summary>
    public async Task<List<ToolResponse>> GetToolsAsync() =>
        await SendAsync<List<ToolResponse>>(HttpMethod.Get, "/api/v1/tools/") ?? [];

    /// <summary>Cria uma tool.</summary>
    public Task<ToolResponse?> CreateToolAsync(ToolUpsertRequest request) =>
        SendAsync<ToolResponse>(HttpMethod.Post, "/api/v1/tools/", request);

    /// <summary>Atualiza uma tool.</summary>
    public Task<ToolResponse?> UpdateToolAsync(string id, ToolUpsertRequest request) =>
        SendAsync<ToolResponse>(HttpMethod.Put, $"/api/v1/tools/{id}", request);

    /// <summary>Remove uma tool.</summary>
    public async Task<bool> DeleteToolAsync(string id) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/tools/{id}");

    // ---------------- Prompts ----------------

    /// <summary>Lista prompts do usuário.</summary>
    public async Task<List<PromptResponse>> GetPromptsAsync() =>
        await SendAsync<List<PromptResponse>>(HttpMethod.Get, "/api/v1/prompts/") ?? [];

    /// <summary>Cria um prompt.</summary>
    public Task<PromptResponse?> CreatePromptAsync(PromptUpsertRequest request) =>
        SendAsync<PromptResponse>(HttpMethod.Post, "/api/v1/prompts/create", request);

    /// <summary>Atualiza um prompt.</summary>
    public Task<PromptResponse?> UpdatePromptAsync(string id, PromptUpsertRequest request) =>
        SendAsync<PromptResponse>(HttpMethod.Post, $"/api/v1/prompts/id/{id}/update", request);

    /// <summary>Remove um prompt.</summary>
    public async Task<bool> DeletePromptAsync(string id)
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, $"/api/v1/prompts/id/{id}/delete");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    // ---------------- Memórias ----------------

    /// <summary>Lista memórias do usuário.</summary>
    public async Task<List<MemoryResponse>> GetMemoriesAsync() =>
        await SendAsync<List<MemoryResponse>>(HttpMethod.Get, "/api/v1/memories/") ?? [];

    /// <summary>Adiciona uma memória.</summary>
    public Task<MemoryResponse?> AddMemoryAsync(string content) =>
        SendAsync<MemoryResponse>(HttpMethod.Post, "/api/v1/memories/add",
            new MemoryUpsertRequest(content));

    /// <summary>Atualiza uma memória.</summary>
    public Task<MemoryResponse?> UpdateMemoryAsync(string id, string content) =>
        SendAsync<MemoryResponse>(HttpMethod.Post, $"/api/v1/memories/{id}/update",
            new MemoryUpsertRequest(content));

    /// <summary>Remove uma memória.</summary>
    public async Task<bool> DeleteMemoryAsync(string id)
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, $"/api/v1/memories/{id}");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    // ---------------- Notas ----------------

    /// <summary>Lista notas do usuário.</summary>
    public async Task<List<NoteResponse>> GetNotesAsync() =>
        await SendAsync<List<NoteResponse>>(HttpMethod.Get, "/api/v1/notes/") ?? [];

    /// <summary>Cria uma nota.</summary>
    public Task<NoteResponse?> CreateNoteAsync(string title, string content) =>
        SendAsync<NoteResponse>(HttpMethod.Post, "/api/v1/notes/create",
            new NoteUpsertRequest(title, content));

    /// <summary>Obtém uma nota.</summary>
    public Task<NoteResponse?> GetNoteAsync(string id) =>
        SendAsync<NoteResponse>(HttpMethod.Get, $"/api/v1/notes/{id}");

    /// <summary>Atualiza uma nota.</summary>
    public Task<NoteResponse?> UpdateNoteAsync(string id, string title, string content) =>
        SendAsync<NoteResponse>(HttpMethod.Post, $"/api/v1/notes/{id}/update",
            new NoteUpsertRequest(title, content));

    /// <summary>Remove uma nota.</summary>
    public async Task<bool> DeleteNoteAsync(string id)
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, $"/api/v1/notes/{id}/delete");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    // ---------------- Arquivos ----------------

    /// <summary>Lista arquivos enviados.</summary>
    public async Task<List<FileResponse>> GetFilesAsync() =>
        await SendAsync<List<FileResponse>>(HttpMethod.Get, "/api/v1/files/") ?? [];

    /// <summary>Envia um arquivo via multipart/form-data.</summary>
    public async Task<FileResponse?> UploadFileAsync(Stream content, string fileName, string? contentType)
    {
        using var form = new MultipartFormDataContent();
        using var streamContent = new StreamContent(content);
        if (!string.IsNullOrEmpty(contentType))
        {
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        form.Add(streamContent, "file", fileName);

        using var request = auth.CreateRequest(HttpMethod.Post, "/api/v1/files/");
        request.Content = form;
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<FileResponse>(JsonOptions)
            : null;
    }

    /// <summary>Remove um arquivo.</summary>
    public async Task<bool> DeleteFileAsync(string id)
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, $"/api/v1/files/{id}");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    // ---------------- Tarefas de IA ----------------

    /// <summary>Gera título automático para o chat.</summary>
    public async Task<string?> GenerateTitleAsync(string model, List<ChatMessageModel> messages, string? chatId)
    {
        var result = await SendAsync<TaskTitleResponse>(HttpMethod.Post,
            "/api/v1/tasks/title/completions",
            new TaskGenerationRequest(
                model,
                messages.Select(m => new ChatCompletionMessage(m.Role, m.Content)).ToList(),
                chatId));
        return result?.Title;
    }

    /// <summary>Gera sugestões de follow-up para a conversa.</summary>
    public async Task<List<string>> GenerateFollowUpsAsync(string model, List<ChatMessageModel> messages, string? chatId)
    {
        var result = await SendAsync<TaskFollowUpsResponse>(HttpMethod.Post,
            "/api/v1/tasks/follow_up/completions",
            new TaskGenerationRequest(
                model,
                messages.Select(m => new ChatCompletionMessage(m.Role, m.Content)).ToList(),
                chatId));
        return result?.FollowUps.ToList() ?? [];
    }

    /// <summary>Gera tags automáticas para o chat.</summary>
    public async Task<List<string>> GenerateTagsAsync(string model, List<ChatMessageModel> messages, string? chatId)
    {
        var result = await SendAsync<TaskTagsResponse>(HttpMethod.Post,
            "/api/v1/tasks/tags/completions",
            new TaskGenerationRequest(
                model,
                messages.Select(m => new ChatCompletionMessage(m.Role, m.Content)).ToList(),
                chatId));
        return result?.Tags.ToList() ?? [];
    }

    // ---------------- Avaliações ----------------

    /// <summary>Registra avaliação (joinha) em uma mensagem.</summary>
    public async Task<bool> SaveFeedbackAsync(
        string chatId, string messageId, string? modelId, int rating, string? reason)
    {
        var result = await SendAsync<FeedbackResponse>(HttpMethod.Post,
            "/api/v1/evaluations/feedback",
            new FeedbackUpsertRequest(chatId, messageId, modelId, rating, reason));
        return result is not null;
    }

    /// <summary>Lista as avaliações do usuário.</summary>
    public async Task<List<FeedbackResponse>> GetMyFeedbacksAsync() =>
        await SendAsync<List<FeedbackResponse>>(HttpMethod.Get, "/api/v1/evaluations/feedbacks/user") ?? [];

    // ---------------- Usuários / Admin ----------------

    /// <summary>Lista usuários (somente admin).</summary>
    public async Task<List<AdminUserResponse>> GetUsersAsync()
    {
        var result = await SendAsync<UsersListResponse>(HttpMethod.Get, "/api/v1/users/");
        return result?.Users.ToList() ?? [];
    }

    /// <summary>Atualiza um usuário (somente admin).</summary>
    public Task<UserResponse?> UpdateUserAsync(string id, AdminUpdateUserRequest request) =>
        SendAsync<UserResponse>(HttpMethod.Post, $"/api/v1/users/{id}/update", request);

    /// <summary>Cadastra um usuário (somente admin).</summary>
    public async Task<bool> AddUserAsync(AddUserRequest request) =>
        await SendStatusAsync(HttpMethod.Post, "/api/v1/auths/add", request);

    /// <summary>Remove um usuário (somente admin).</summary>
    public async Task<bool> DeleteUserAsync(string id)
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, $"/api/v1/users/{id}");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Obtém as configurações de UI persistidas do usuário.</summary>
    public async Task<Dictionary<string, object>?> GetUserSettingsAsync() =>
        await SendAsync<Dictionary<string, object>>(HttpMethod.Get, "/api/v1/users/user/settings");

    /// <summary>Atualiza as configurações de UI do usuário.</summary>
    public async Task<bool> UpdateUserSettingsAsync(object settings) =>
        await SendStatusAsync(HttpMethod.Post, "/api/v1/users/user/settings/update", settings);

    /// <summary>Obtém a configuração pública da aplicação.</summary>
    public Task<AppConfigResponse?> GetAppConfigAsync() =>
        SendAsync<AppConfigResponse>(HttpMethod.Get, "/api/config");

    private AppConfigResponse? _appConfigCache;

    /// <summary>Obtém a configuração pública com cache em memória (uma chamada por sessão).</summary>
    public async Task<AppConfigResponse?> GetAppConfigCachedAsync() =>
        _appConfigCache ??= await GetAppConfigAsync();

    /// <summary>Obtém a configuração administrativa (somente admin).</summary>
    public Task<AdminConfig?> GetAdminConfigAsync() =>
        SendAsync<AdminConfig>(HttpMethod.Get, "/api/v1/auths/admin/config");

    /// <summary>Atualiza a configuração administrativa (somente admin).</summary>
    public Task<AdminConfig?> UpdateAdminConfigAsync(AdminConfig config) =>
        SendAsync<AdminConfig>(HttpMethod.Post, "/api/v1/auths/admin/config", config);

    /// <summary>Atualiza nome e imagem de perfil do usuário.</summary>
    public Task<UserResponse?> UpdateProfileAsync(string name, string? profileImageUrl) =>
        SendAsync<UserResponse>(HttpMethod.Post, "/api/v1/auths/update/profile",
            new { name, profileImageUrl });

    /// <summary>Atualiza a senha do usuário.</summary>
    public async Task<bool> UpdatePasswordAsync(string current, string password) =>
        await SendStatusAsync(HttpMethod.Post, "/api/v1/auths/update/password",
            new UpdatePasswordRequest(current, password));

    /// <summary>Gera uma nova chave de API do usuário.</summary>
    public Task<ApiKeyCreatedResponse?> CreateApiKeyAsync() =>
        SendAsync<ApiKeyCreatedResponse>(HttpMethod.Post, "/api/v1/auths/api_key");

    /// <summary>Obtém metadados da chave de API ativa.</summary>
    public Task<ApiKeyInfoResponse?> GetApiKeyAsync() =>
        SendAsync<ApiKeyInfoResponse>(HttpMethod.Get, "/api/v1/auths/api_key");

    /// <summary>Revoga a chave de API do usuário.</summary>
    public async Task<bool> DeleteApiKeyAsync()
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, "/api/v1/auths/api_key");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    // ---------------- Conexões e versão ----------------

    /// <summary>Obtém a configuração de conexões (somente admin).</summary>
    public Task<ConnectionsConfigResponse?> GetConnectionsAsync() =>
        SendAsync<ConnectionsConfigResponse>(HttpMethod.Get, "/api/v1/configs/connections");

    /// <summary>Atualiza a configuração de conexões (somente admin).</summary>
    public Task<ConnectionsConfigResponse?> UpdateConnectionsAsync(ConnectionsConfig config) =>
        SendAsync<ConnectionsConfigResponse>(HttpMethod.Post, "/api/v1/configs/connections", config);

    /// <summary>Obtém a versão do backend.</summary>
    public Task<VersionResponse?> GetVersionAsync() =>
        SendAsync<VersionResponse>(HttpMethod.Get, "/api/version");

    // ---------------- Geração de imagens ----------------

    /// <summary>Obtém a configuração de geração de imagens (somente admin, chave mascarada).</summary>
    public Task<ImagesConfig?> GetImagesConfigAsync() =>
        SendAsync<ImagesConfig>(HttpMethod.Get, "/api/v1/images/config");

    /// <summary>Atualiza a configuração de geração de imagens (somente admin).</summary>
    public Task<ImagesConfig?> UpdateImagesConfigAsync(ImagesConfig config) =>
        SendAsync<ImagesConfig>(HttpMethod.Post, "/api/v1/images/config", config);

    /// <summary>Gera imagens a partir de um prompt; nulo quando falha ou feature off.</summary>
    public Task<List<GeneratedImage>?> GenerateImagesAsync(string prompt, string? size = null) =>
        SendAsync<List<GeneratedImage>>(HttpMethod.Post, "/api/v1/images/generations",
            new ImageGenerationRequest(prompt, 1, size));

    // ---------------- Admin: avaliações e flags ----------------

    /// <summary>Lista todas as avaliações com identificação do usuário (somente admin).</summary>
    public Task<List<AdminFeedbackResponse>?> GetAllFeedbacksAsync() =>
        SendAsync<List<AdminFeedbackResponse>>(HttpMethod.Get, "/api/v1/evaluations/feedbacks/list");

    /// <summary>Persiste feature flags administráveis (somente admin).</summary>
    public Task<AdminConfig?> UpdateAppConfigAsync(AdminConfig config) =>
        SendAsync<AdminConfig>(HttpMethod.Post, "/api/config", config);

    // ---------------- Internos ----------------

    private async Task<bool> SendStatusAsync(HttpMethod method, string uri, object? body = null)
    {
        using var request = auth.CreateRequest(method, uri);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string uri, object? body = null)
    {
        using var request = auth.CreateRequest(method, uri);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
    }

    // ---------------- Analytics (admin) ----------------

    /// <summary>Dashboard de analytics agregado (admin, apenas contagens).</summary>
    public Task<AnalyticsResponse?> GetAnalyticsAsync(int days = 30) =>
        SendAsync<AnalyticsResponse>(HttpMethod.Get, $"/api/v1/analytics?days={days}");

    // ---------------- Grupos (RBAC) ----------------

    /// <summary>Lista os grupos visíveis ao usuário (admin vê todos).</summary>
    public async Task<List<GroupResponse>> GetGroupsAsync() =>
        await SendAsync<List<GroupResponse>>(HttpMethod.Get, "/api/v1/groups") ?? [];

    /// <summary>Cria um grupo (admin).</summary>
    public Task<GroupResponse?> CreateGroupAsync(CreateGroupRequest request) =>
        SendAsync<GroupResponse>(HttpMethod.Post, "/api/v1/groups", request);

    /// <summary>Atualiza nome/descrição/permissões de um grupo.</summary>
    public Task<GroupResponse?> UpdateGroupAsync(string id, UpdateGroupRequest request) =>
        SendAsync<GroupResponse>(HttpMethod.Put, $"/api/v1/groups/{id}", request);

    /// <summary>Exclui um grupo (admin).</summary>
    public async Task<bool> DeleteGroupAsync(string id) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/groups/{id}");

    /// <summary>Adiciona membros a um grupo.</summary>
    public Task<GroupResponse?> AddGroupMembersAsync(string id, UpdateGroupMembersRequest request) =>
        SendAsync<GroupResponse>(HttpMethod.Post, $"/api/v1/groups/{id}/members", request);

    /// <summary>Remove um membro do grupo.</summary>
    public async Task<bool> RemoveGroupMemberAsync(string id, string userId) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/groups/{id}/members/{userId}");

    // ---------------- Knowledge (RAG) ----------------

    /// <summary>Lista as coleções de knowledge do usuário.</summary>
    public async Task<List<KnowledgeResponse>> GetKnowledgeAsync() =>
        await SendAsync<List<KnowledgeResponse>>(HttpMethod.Get, "/api/v1/knowledge") ?? [];

    /// <summary>Cria uma coleção de knowledge.</summary>
    public Task<KnowledgeResponse?> CreateKnowledgeAsync(string name, string? description = null) =>
        SendAsync<KnowledgeResponse>(HttpMethod.Post, "/api/v1/knowledge",
            new CreateKnowledgeRequest(name, description));

    /// <summary>Obtém uma coleção com seus arquivos.</summary>
    public Task<KnowledgeDetailResponse?> GetKnowledgeDetailAsync(string id) =>
        SendAsync<KnowledgeDetailResponse>(HttpMethod.Get, $"/api/v1/knowledge/{id}");

    /// <summary>Exclui uma coleção de knowledge.</summary>
    public async Task<bool> DeleteKnowledgeAsync(string id) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/knowledge/{id}");

    /// <summary>Vincula um arquivo já enviado a uma coleção.</summary>
    public Task<bool> AddKnowledgeFileAsync(string id, string fileId) =>
        SendStatusAsync(HttpMethod.Post, $"/api/v1/knowledge/{id}/files",
            new AddKnowledgeFileRequest(fileId));

    /// <summary>Remove um arquivo de uma coleção.</summary>
    public async Task<bool> RemoveKnowledgeFileAsync(string id, string fileId) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/knowledge/{id}/files/{fileId}");

    // ---------------- Canais ----------------

    /// <summary>Lista os canais onde o usuário é membro.</summary>
    public async Task<List<ChannelResponse>> GetChannelsAsync() =>
        await SendAsync<List<ChannelResponse>>(HttpMethod.Get, "/api/v1/channels") ?? [];

    /// <summary>Cria um canal e retorna o resumo.</summary>
    public Task<ChannelResponse?> CreateChannelAsync(string name, string? description = null) =>
        SendAsync<ChannelResponse>(HttpMethod.Post, "/api/v1/channels",
            new CreateChannelRequest(name, description, null));

    /// <summary>Obtém um canal com seus membros.</summary>
    public Task<ChannelDetailResponse?> GetChannelAsync(string id) =>
        SendAsync<ChannelDetailResponse>(HttpMethod.Get, $"/api/v1/channels/{id}");

    /// <summary>Lista o histórico de mensagens do canal.</summary>
    public async Task<List<ChannelMessageResponse>> GetChannelMessagesAsync(string id) =>
        await SendAsync<List<ChannelMessageResponse>>(
            HttpMethod.Get, $"/api/v1/channels/{id}/messages?take=200") ?? [];

    /// <summary>Envia mensagem ao canal (retorna a mensagem persistida).</summary>
    public Task<ChannelMessageResponse?> PostChannelMessageAsync(string id, string content) =>
        SendAsync<ChannelMessageResponse>(HttpMethod.Post,
            $"/api/v1/channels/{id}/messages", new CreateChannelMessageRequest(content));

    /// <summary>Adiciona um usuário ao canal.</summary>
    public Task<bool> AddChannelMemberAsync(string id, string userId) =>
        SendStatusAsync(HttpMethod.Post, $"/api/v1/channels/{id}/members",
            new AddChannelMembersRequest([userId], null));

    private sealed record UsersListResponse(List<AdminUserResponse> Users, int Total);
}

/// <summary>Pasta com os chats contidos (forma da resposta de /api/v1/folders).</summary>
public class FolderWithChats
{
    /// <summary>Identificador da pasta.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Nome da pasta.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Chats dentro da pasta.</summary>
    public List<ChatSummaryResponse> Chats { get; } = [];

    /// <summary>Se a pasta está expandida na UI.</summary>
    public bool Expanded { get; set; } = true;
}

/// <summary>Chat compartilhado publicamente.</summary>
/// <param name="Id">Identificador do chat.</param>
/// <param name="Title">Título.</param>
/// <param name="User">Autor do chat.</param>
/// <param name="Models">Modelos usados.</param>
/// <param name="Messages">Mensagens.</param>
/// <param name="CreatedAt">Criação.</param>
/// <param name="UpdatedAt">Última atualização.</param>
public sealed record SharedChatResponse(
    string Id,
    string Title,
    SharedChatUser? User,
    IReadOnlyList<string> Models,
    IReadOnlyList<ChatMessageModel> Messages,
    long CreatedAt,
    long UpdatedAt);

/// <summary>Autor de um chat compartilhado.</summary>
/// <param name="Name">Nome de exibição.</param>
public sealed record SharedChatUser(string? Name);
