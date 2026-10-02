using System.Net.Http.Json;
using System.Text.Json.Nodes;
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

    /// <summary>Lista usuários paginada com busca/filtro/ordenação (somente admin).</summary>
    public async Task<UsersPage> GetUsersAsync(
        string? query = null, string? filter = null,
        string? orderBy = null, string? direction = null,
        int page = 1, int perPage = 20)
    {
        var uri = $"/api/v1/users/?page={page}&per_page={perPage}"
            + (string.IsNullOrWhiteSpace(query) ? "" : $"&query={Uri.EscapeDataString(query)}")
            + (string.IsNullOrWhiteSpace(filter) ? "" : $"&filter={Uri.EscapeDataString(filter)}")
            + (string.IsNullOrWhiteSpace(orderBy) ? "" : $"&order_by={Uri.EscapeDataString(orderBy)}")
            + (string.IsNullOrWhiteSpace(direction) ? "" : $"&direction={Uri.EscapeDataString(direction)}");
        var result = await SendAsync<UsersListResponse>(HttpMethod.Get, uri);
        return new UsersPage(result?.Users.ToList() ?? [], result?.Total ?? 0, result?.Page ?? page);
    }

    /// <summary>Permissões granulares de um usuário (somente admin).</summary>
    public async Task<JsonObject?> GetUserPermissionsAsync(string id) =>
        await SendAsync<JsonObject>(HttpMethod.Get, $"/api/v1/users/{id}/permissions");

    /// <summary>Atualiza permissões granulares de um usuário (somente admin).</summary>
    public Task<UserResponse?> UpdateUserPermissionsAsync(string id, JsonObject permissions) =>
        SendAsync<UserResponse>(HttpMethod.Put, $"/api/v1/users/{id}/permissions", permissions);

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

    /// <summary>Obtém o webhook de notificação do usuário.</summary>
    public Task<NotificationWebhookResponse?> GetWebhookAsync() =>
        SendAsync<NotificationWebhookResponse>(HttpMethod.Get, "/api/v1/notifications/webhook");

    /// <summary>Salva o webhook de notificação do usuário.</summary>
    public Task<NotificationWebhookResponse?> SaveWebhookAsync(NotificationWebhookRequest request) =>
        SendAsync<NotificationWebhookResponse>(HttpMethod.Post, "/api/v1/notifications/webhook", request);

    /// <summary>Remove o webhook de notificação do usuário.</summary>
    public async Task<bool> DeleteWebhookAsync()
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, "/api/v1/notifications/webhook");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Dispara um evento de teste para o webhook do usuário.</summary>
    public Task<WebhookTestResponse?> TestWebhookAsync() =>
        SendAsync<WebhookTestResponse>(HttpMethod.Post, "/api/v1/notifications/webhook/test");

    /// <summary>Obtém o webhook global (somente admin).</summary>
    public Task<NotificationWebhookResponse?> GetAdminWebhookAsync() =>
        SendAsync<NotificationWebhookResponse>(HttpMethod.Get, "/api/v1/notifications/admin/webhook");

    /// <summary>Salva o webhook global (somente admin).</summary>
    public Task<NotificationWebhookResponse?> SaveAdminWebhookAsync(NotificationWebhookRequest request) =>
        SendAsync<NotificationWebhookResponse>(HttpMethod.Post, "/api/v1/notifications/admin/webhook", request);

    /// <summary>Remove o webhook global (somente admin).</summary>
    public async Task<bool> DeleteAdminWebhookAsync()
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, "/api/v1/notifications/admin/webhook");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Dispara um evento de teste para o webhook global (somente admin).</summary>
    public Task<WebhookTestResponse?> TestAdminWebhookAsync() =>
        SendAsync<WebhookTestResponse>(HttpMethod.Post, "/api/v1/notifications/admin/webhook/test");

    /// <summary>Lista terminal servers configurados.</summary>
    public Task<List<TerminalServerResponse>?> GetTerminalServersAsync() =>
        SendAsync<List<TerminalServerResponse>>(HttpMethod.Get, "/api/v1/terminals/");

    /// <summary>Cria/atualiza um terminal server (admin).</summary>
    public Task<TerminalServerResponse?> SaveTerminalServerAsync(TerminalServerRequest request) =>
        SendAsync<TerminalServerResponse>(HttpMethod.Post, "/api/v1/terminals/config", request);

    /// <summary>Remove um terminal server (admin).</summary>
    public Task<bool> DeleteTerminalServerAsync(string id) =>
        SendStatusAsync(HttpMethod.Delete, $"/api/v1/terminals/config/{id}");

    /// <summary>Configuração SAML (admin).</summary>
    public Task<SamlConfigResponse?> GetSamlConfigAsync() =>
        SendAsync<SamlConfigResponse>(HttpMethod.Get, "/api/v1/configs/saml");

    /// <summary>Salva a configuração SAML (admin).</summary>
    public Task<SamlConfigResponse?> SaveSamlConfigAsync(SamlConfigRequest request) =>
        SendAsync<SamlConfigResponse>(HttpMethod.Post, "/api/v1/configs/saml", request);

    /// <summary>Configuração SCIM (admin).</summary>
    public Task<ScimConfigResponse?> GetScimConfigAsync() =>
        SendAsync<ScimConfigResponse>(HttpMethod.Get, "/api/v1/configs/scim");

    /// <summary>Salva a configuração SCIM (admin).</summary>
    public Task<ScimConfigResponse?> SaveScimConfigAsync(ScimConfigRequest request) =>
        SendAsync<ScimConfigResponse>(HttpMethod.Post, "/api/v1/configs/scim", request);

    /// <summary>Lista skills do usuário (admin vê todas).</summary>
    public Task<List<SkillResponse>?> GetSkillsAsync() =>
        SendAsync<List<SkillResponse>>(HttpMethod.Get, "/api/v1/skills/");

    /// <summary>Cria uma skill.</summary>
    public Task<SkillResponse?> CreateSkillAsync(SkillRequest request) =>
        SendAsync<SkillResponse>(HttpMethod.Post, "/api/v1/skills/", request);

    /// <summary>Atualiza uma skill.</summary>
    public Task<SkillResponse?> UpdateSkillAsync(string id, SkillRequest request) =>
        SendAsync<SkillResponse>(HttpMethod.Post, $"/api/v1/skills/{id}", request);

    /// <summary>Remove uma skill.</summary>
    public Task<bool> DeleteSkillAsync(string id) =>
        SendStatusAsync(HttpMethod.Delete, $"/api/v1/skills/{id}");

    /// <summary>Lista functions registradas (admin).</summary>
    public Task<List<FunctionResponse>?> GetFunctionsAsync() =>
        SendAsync<List<FunctionResponse>>(HttpMethod.Get, "/api/v1/functions/");

    /// <summary>Cria uma function (admin).</summary>
    public Task<FunctionResponse?> CreateFunctionAsync(FunctionRequest request) =>
        SendAsync<FunctionResponse>(HttpMethod.Post, "/api/v1/functions/", request);

    /// <summary>Atualiza uma function (admin).</summary>
    public Task<FunctionResponse?> UpdateFunctionAsync(string id, FunctionRequest request) =>
        SendAsync<FunctionResponse>(HttpMethod.Post, $"/api/v1/functions/{id}", request);

    /// <summary>Alterna ativação de uma function (admin).</summary>
    public Task<FunctionResponse?> ToggleFunctionAsync(string id) =>
        SendAsync<FunctionResponse>(HttpMethod.Post, $"/api/v1/functions/{id}/toggle");

    /// <summary>Salva os valves de uma function (admin).</summary>
    public Task<FunctionResponse?> SaveFunctionValvesAsync(string id, string valvesJson) =>
        SendAsync<FunctionResponse>(HttpMethod.Post, $"/api/v1/functions/{id}/valves",
            new FunctionValvesRequest(valvesJson));

    /// <summary>Remove uma function (admin).</summary>
    public Task<bool> DeleteFunctionAsync(string id) =>
        SendStatusAsync(HttpMethod.Delete, $"/api/v1/functions/{id}");

    /// <summary>Lista servidores de pipelines (admin).</summary>
    public Task<List<PipelineServerResponse>?> GetPipelineServersAsync() =>
        SendAsync<List<PipelineServerResponse>>(HttpMethod.Get, "/api/v1/pipelines/");

    /// <summary>Registra/atualiza um servidor de pipelines (admin).</summary>
    public Task<PipelineServerResponse?> SavePipelineServerAsync(PipelineServerRequest request) =>
        SendAsync<PipelineServerResponse>(HttpMethod.Post, "/api/v1/pipelines/", request);

    /// <summary>Remove um servidor de pipelines (admin).</summary>
    public Task<bool> DeletePipelineServerAsync(string id) =>
        SendStatusAsync(HttpMethod.Delete, $"/api/v1/pipelines/{id}");

    /// <summary>Lista pipes descobertos em todos os servidores.</summary>
    public Task<List<PipelinePipeResponse>?> GetPipelinePipesAsync() =>
        SendAsync<List<PipelinePipeResponse>>(HttpMethod.Get, "/api/v1/pipelines/list");

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

    /// <summary>Testa a conectividade do motor de imagens configurado (admin).</summary>
    public Task<ImageTestResponse?> TestImagesConfigAsync() =>
        SendAsync<ImageTestResponse>(HttpMethod.Post, "/api/v1/images/config/test");

    /// <summary>Edita uma imagem existente com um prompt (engines com suporte).</summary>
    public Task<GeneratedImage?> EditImageAsync(string imageId, string prompt, string? size = null) =>
        SendAsync<GeneratedImage>(HttpMethod.Post, "/api/v1/images/edit",
            new ImageEditRequest(imageId, prompt, size));

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
    public Task<ChannelMessageResponse?> PostChannelMessageAsync(
        string id, string content, string? parentId = null) =>
        SendAsync<ChannelMessageResponse>(HttpMethod.Post,
            $"/api/v1/channels/{id}/messages",
            new CreateChannelMessageRequest(content, parentId));

    /// <summary>Cria ou obtém o DM com outro usuário.</summary>
    public Task<ChannelResponse?> CreateDmAsync(string userId) =>
        SendAsync<ChannelResponse>(HttpMethod.Post,
            "/api/v1/channels/dm", new CreateDmRequest(userId));

    /// <summary>Marca o canal como lido.</summary>
    public Task<bool> MarkChannelReadAsync(string id) =>
        SendStatusAsync(HttpMethod.Post, $"/api/v1/channels/{id}/read");

    /// <summary>Lista respostas (thread) de uma mensagem.</summary>
    public async Task<List<ChannelMessageResponse>> GetChannelRepliesAsync(
        string id, string messageId) =>
        await SendAsync<List<ChannelMessageResponse>>(
            HttpMethod.Get, $"/api/v1/channels/{id}/messages/{messageId}/replies") ?? [];

    /// <summary>Adiciona reação; retorna o agregado atualizado.</summary>
    public async Task<List<ChannelReactionResponse>> AddChannelReactionAsync(
        string id, string messageId, string emoji) =>
        await SendAsync<List<ChannelReactionResponse>>(HttpMethod.Post,
            $"/api/v1/channels/{id}/messages/{messageId}/reactions/{Uri.EscapeDataString(emoji)}") ?? [];

    /// <summary>Remove reação; retorna o agregado atualizado.</summary>
    public async Task<List<ChannelReactionResponse>> RemoveChannelReactionAsync(
        string id, string messageId, string emoji) =>
        await SendAsync<List<ChannelReactionResponse>>(HttpMethod.Delete,
            $"/api/v1/channels/{id}/messages/{messageId}/reactions/{Uri.EscapeDataString(emoji)}") ?? [];

    /// <summary>Fixa/desfixa mensagem.</summary>
    public Task<bool> SetChannelMessagePinnedAsync(string id, string messageId, bool pinned) =>
        SendStatusAsync(pinned ? HttpMethod.Post : HttpMethod.Delete,
            $"/api/v1/channels/{id}/messages/{messageId}/pin");

    /// <summary>Lista mensagens fixadas.</summary>
    public async Task<List<ChannelMessageResponse>> GetChannelPinnedAsync(string id) =>
        await SendAsync<List<ChannelMessageResponse>>(
            HttpMethod.Get, $"/api/v1/channels/{id}/pinned") ?? [];

    /// <summary>Adiciona um usuário ao canal.</summary>
    public Task<bool> AddChannelMemberAsync(string id, string userId) =>
        SendStatusAsync(HttpMethod.Post, $"/api/v1/channels/{id}/members",
            new AddChannelMembersRequest([userId], null));

    /// <summary>Lista automações do usuário.</summary>
    public async Task<List<AutomationResponse>> GetAutomationsAsync() =>
        await SendAsync<List<AutomationResponse>>(HttpMethod.Get, "/api/v1/automations") ?? [];

    /// <summary>Cria automação.</summary>
    public Task<AutomationResponse?> CreateAutomationAsync(AutomationUpsertRequest request) =>
        SendAsync<AutomationResponse>(HttpMethod.Post, "/api/v1/automations", request);

    /// <summary>Atualiza automação.</summary>
    public Task<AutomationResponse?> UpdateAutomationAsync(string id, AutomationUpsertRequest request) =>
        SendAsync<AutomationResponse>(HttpMethod.Put, $"/api/v1/automations/{id}", request);

    /// <summary>Remove automação.</summary>
    public Task<bool> DeleteAutomationAsync(string id) =>
        SendStatusAsync(HttpMethod.Delete, $"/api/v1/automations/{id}");

    /// <summary>Lista runs de uma automação.</summary>
    public async Task<List<AutomationRunResponse>> GetAutomationRunsAsync(string id) =>
        await SendAsync<List<AutomationRunResponse>>(
            HttpMethod.Get, $"/api/v1/automations/{id}/runs") ?? [];

    /// <summary>Lista runs do usuário num intervalo epoch (calendário).</summary>
    public async Task<List<AutomationRunResponse>> GetAutomationRunsInRangeAsync(long from, long to) =>
        await SendAsync<List<AutomationRunResponse>>(
            HttpMethod.Get, $"/api/v1/automations/runs?from={from}&to={to}") ?? [];

    /// <summary>Dispara uma execução imediata.</summary>
    public Task<AutomationRunResponse?> RunAutomationNowAsync(string id) =>
        SendAsync<AutomationRunResponse>(HttpMethod.Post, $"/api/v1/automations/{id}/run-now");

    // ---------------- Rotas dedicadas (detalhe por id) ----------------

    /// <summary>Obtém um prompt pelo id.</summary>
    public Task<PromptResponse?> GetPromptAsync(string id) =>
        SendAsync<PromptResponse>(HttpMethod.Get, $"/api/v1/prompts/id/{Uri.EscapeDataString(id)}");

    /// <summary>Obtém uma automação pelo id.</summary>
    public Task<AutomationResponse?> GetAutomationAsync(string id) =>
        SendAsync<AutomationResponse>(HttpMethod.Get, $"/api/v1/automations/{Uri.EscapeDataString(id)}");

    /// <summary>Obtém um modelo customizado pelo id.</summary>
    public Task<ModelEntryResponse?> GetCustomModelAsync(string id) =>
        SendAsync<ModelEntryResponse>(HttpMethod.Get,
            $"/api/v1/models/model?id={Uri.EscapeDataString(id)}");

    /// <summary>Obtém uma pasta pelo id.</summary>
    public Task<FolderResponse?> GetFolderAsync(string id) =>
        SendAsync<FolderResponse>(HttpMethod.Get, $"/api/v1/folders/{Uri.EscapeDataString(id)}");

    /// <summary>Lista os chats de uma pasta.</summary>
    public async Task<List<ChatSummaryResponse>> GetFolderChatsAsync(string id) =>
        await SendAsync<List<ChatSummaryResponse>>(
            HttpMethod.Get, $"/api/v1/chats/folder/{Uri.EscapeDataString(id)}") ?? [];

    /// <summary>Atualiza nome/descrição de uma coleção de knowledge.</summary>
    public Task<KnowledgeResponse?> UpdateKnowledgeAsync(string id, string name, string? description) =>
        SendAsync<KnowledgeResponse>(HttpMethod.Put, $"/api/v1/knowledge/{Uri.EscapeDataString(id)}",
            new UpdateKnowledgeRequest(name, description));

    // ---------------- Configurações admin (/api/v1/configs) ----------------

    /// <summary>Lista banners ativos (qualquer usuário autenticado).</summary>
    public async Task<List<BannerResponse>> GetBannersAsync() =>
        await SendAsync<List<BannerResponse>>(HttpMethod.Get, "/api/v1/configs/banners") ?? [];

    /// <summary>Cria um banner (somente admin).</summary>
    public Task<BannerResponse?> CreateBannerAsync(BannerRequest request) =>
        SendAsync<BannerResponse>(HttpMethod.Post, "/api/v1/configs/banners", request);

    /// <summary>Atualiza um banner (somente admin).</summary>
    public Task<BannerResponse?> UpdateBannerAsync(string id, BannerRequest request) =>
        SendAsync<BannerResponse>(HttpMethod.Put, $"/api/v1/configs/banners/{id}", request);

    /// <summary>Remove um banner (somente admin).</summary>
    public async Task<bool> DeleteBannerAsync(string id) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/configs/banners/{id}");

    /// <summary>Obtém default models + sugestões de prompt.</summary>
    public Task<ModelsConfig?> GetModelsConfigAsync() =>
        SendAsync<ModelsConfig>(HttpMethod.Get, "/api/v1/configs/models");

    /// <summary>Atualiza default models + sugestões (somente admin).</summary>
    public Task<ModelsConfig?> UpdateModelsConfigAsync(ModelsConfig config) =>
        SendAsync<ModelsConfig>(HttpMethod.Post, "/api/v1/configs/models", config);

    /// <summary>Lê um toggle/config de feature ("channels", "direct_connections").</summary>
    public async Task<bool> GetFeatureToggleAsync(string name, bool fallback = false) =>
        (await SendAsync<FeatureToggle>(HttpMethod.Get, $"/api/v1/configs/{name}"))?.Enabled ?? fallback;

    /// <summary>Grava um toggle de feature (somente admin).</summary>
    public async Task<bool> SetFeatureToggleAsync(string name, bool enabled) =>
        await SendAsync<FeatureToggle>(HttpMethod.Post, $"/api/v1/configs/{name}",
            new FeatureToggle(enabled)) is not null;

    /// <summary>Obtém a configuração de cadastro (somente admin para escrita).</summary>
    public Task<SignupConfig?> GetSignupConfigAsync() =>
        SendAsync<SignupConfig>(HttpMethod.Get, "/api/v1/configs/signup");

    /// <summary>Atualiza a configuração de cadastro (somente admin).</summary>
    public Task<SignupConfig?> UpdateSignupConfigAsync(SignupConfig config) =>
        SendAsync<SignupConfig>(HttpMethod.Post, "/api/v1/configs/signup", config);

    /// <summary>Obtém a configuração de execução de código.</summary>
    public Task<CodeExecutionConfig?> GetCodeExecutionConfigAsync() =>
        SendAsync<CodeExecutionConfig>(HttpMethod.Get, "/api/v1/configs/code_execution");

    /// <summary>Atualiza a configuração de execução de código (somente admin).</summary>
    public Task<CodeExecutionConfig?> UpdateCodeExecutionConfigAsync(CodeExecutionConfig config) =>
        SendAsync<CodeExecutionConfig>(HttpMethod.Post, "/api/v1/configs/code_execution", config);

    /// <summary>Obtém a duração configurada do JWT.</summary>
    public Task<JwtExpiryConfig?> GetJwtExpiryAsync() =>
        SendAsync<JwtExpiryConfig>(HttpMethod.Get, "/api/v1/configs/jwt");

    /// <summary>Atualiza a duração do JWT (somente admin).</summary>
    public Task<JwtExpiryConfig?> UpdateJwtExpiryAsync(JwtExpiryConfig config) =>
        SendAsync<JwtExpiryConfig>(HttpMethod.Post, "/api/v1/configs/jwt", config);

    /// <summary>Obtém a config de áudio (admin).</summary>
    public Task<AudioConfig?> GetAudioConfigAsync() =>
        SendAsync<AudioConfig>(HttpMethod.Get, "/api/v1/audio/config");

    /// <summary>Atualiza a config de áudio (admin).</summary>
    public Task<AudioConfig?> UpdateAudioConfigAsync(AudioConfig config) =>
        SendAsync<AudioConfig>(HttpMethod.Post, "/api/v1/audio/config", config);
    /// <summary>Obtém a config de retrieval (admin).</summary>
    public Task<RetrievalConfig?> GetRetrievalConfigAsync() =>
        SendAsync<RetrievalConfig>(HttpMethod.Get, "/api/v1/retrieval/config");

    /// <summary>Atualiza a config de retrieval (admin).</summary>
    public Task<RetrievalConfig?> UpdateRetrievalConfigAsync(RetrievalConfig config) =>
        SendAsync<RetrievalConfig>(HttpMethod.Post, "/api/v1/retrieval/config/update", config);

    // ---------- Access grants + calendários ----------

    private sealed record AccessGrantsResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("access_grants")]
        List<AccessGrant> AccessGrants);

    /// <summary>Obtém os grants de acesso de um recurso (somente dono/admin).</summary>
    public async Task<List<AccessGrant>?> GetAccessGrantsAsync(string entity, string id)
    {
        var response = await SendAsync<AccessGrantsResponse>(HttpMethod.Get,
            $"/api/v1/{entity}/{id}/access");
        return response?.AccessGrants;
    }

    /// <summary>Atualiza os grants de acesso de um recurso (knowledge|notes|models|calendars).</summary>
    public async Task<bool> UpdateAccessGrantsAsync(string entity, string id, List<AccessGrant> grants) =>
        await SendStatusAsync(HttpMethod.Post, $"/api/v1/{entity}/{id}/access/update",
            new AccessUpdateRequest(grants));

    /// <summary>Lista calendários visíveis (próprios + compartilhados).</summary>
    public async Task<List<CalendarResponse>> GetCalendarsAsync() =>
        await SendAsync<List<CalendarResponse>>(HttpMethod.Get, "/api/v1/calendars/") ?? [];

    /// <summary>Cria um calendário.</summary>
    public Task<CalendarResponse?> CreateCalendarAsync(string name, string? color) =>
        SendAsync<CalendarResponse>(HttpMethod.Post, "/api/v1/calendars/",
            new CreateCalendarRequest(name, color));

    /// <summary>Exclui um calendário (somente dono/admin).</summary>
    public async Task<bool> DeleteCalendarAsync(string id) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/calendars/{id}");

    /// <summary>Lista eventos de todos os calendários visíveis num intervalo.</summary>
    public async Task<List<CalendarEventResponse>> GetCalendarEventsAsync(long? from = null, long? to = null)
    {
        var q = from is not null || to is not null ? $"?from={from}&to={to}" : string.Empty;
        return await SendAsync<List<CalendarEventResponse>>(HttpMethod.Get,
            $"/api/v1/calendars/events{q}") ?? [];
    }

    /// <summary>Cria um evento num calendário.</summary>
    public Task<CalendarEventResponse?> CreateCalendarEventAsync(
        string calendarId, string title, long startTs, long endTs, string? color = null, string? notes = null) =>
        SendAsync<CalendarEventResponse>(HttpMethod.Post, "/api/v1/calendars/events",
            new CreateEventRequest(calendarId, title, startTs, endTs, color, notes));

    /// <summary>Exclui um evento.</summary>
    public async Task<bool> DeleteCalendarEventAsync(string eventId) =>
        await SendStatusAsync(HttpMethod.Delete, $"/api/v1/calendars/events/{eventId}");

    // ---------- Arena ----------

    /// <summary>Registra o voto de uma batalha de arena e revela os modelos.</summary>
    public Task<ArenaFeedbackResponse?> VoteArenaAsync(string battleId, string winner) =>
        SendAsync<ArenaFeedbackResponse>(HttpMethod.Post,
            "/api/v1/evaluations/arena/feedback",
            new ArenaFeedbackRequest(battleId, winner));

    /// <summary>Leaderboard de arena (ELO) — admin.</summary>
    public async Task<List<LeaderboardEntryResponse>> GetLeaderboardAsync() =>
        await SendAsync<List<LeaderboardEntryResponse>>(
            HttpMethod.Get, "/api/v1/evaluations/leaderboard") ?? [];

    private sealed record UsersListResponse(List<AdminUserResponse> Users, int Total, int Page = 1);
}

/// <summary>Página de usuários administráveis.</summary>
/// <param name="Users">Usuários da página.</param>
/// <param name="Total">Total de usuários que casam com o filtro.</param>
/// <param name="Page">Página retornada.</param>
public sealed record UsersPage(List<AdminUserResponse> Users, int Total, int Page);

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
