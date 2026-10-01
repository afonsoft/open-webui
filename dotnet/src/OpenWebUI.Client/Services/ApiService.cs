using System.Net.Http.Json;
using OpenWebUI.Shared.Contracts;

namespace OpenWebUI.Client.Services;

/// <summary>Cliente HTTP tipado para a API do backend .NET.</summary>
public class ApiService(HttpClient http, AuthService auth)
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Lista os chats do usuário, opcionalmente filtrados por texto.</summary>
    public async Task<List<ChatSummaryResponse>> GetChatsAsync(string? query = null)
    {
        var uri = string.IsNullOrWhiteSpace(query)
            ? "/api/v1/chats/"
            : $"/api/v1/chats/?query={Uri.EscapeDataString(query)}";
        return await SendAsync<List<ChatSummaryResponse>>(HttpMethod.Get, uri) ?? [];
    }

    /// <summary>Obtém um chat completo com mensagens.</summary>
    public Task<ChatResponse?> GetChatAsync(string id) =>
        SendAsync<ChatResponse>(HttpMethod.Get, $"/api/v1/chats/{id}");

    /// <summary>Cria um chat e retorna o recurso criado.</summary>
    public Task<ChatResponse?> CreateChatAsync(ChatUpsertRequest request) =>
        SendAsync<ChatResponse>(HttpMethod.Post, "/api/v1/chats/", request);

    /// <summary>Atualiza título, modelos e mensagens de um chat.</summary>
    public Task<ChatResponse?> UpdateChatAsync(string id, ChatUpsertRequest request) =>
        SendAsync<ChatResponse>(HttpMethod.Post, $"/api/v1/chats/{id}", request);

    /// <summary>Remove um chat.</summary>
    public async Task<bool> DeleteChatAsync(string id)
    {
        using var request = auth.CreateRequest(HttpMethod.Delete, $"/api/v1/chats/{id}");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Lista modelos disponíveis em todos os provedores.</summary>
    public async Task<List<ModelInfo>> GetModelsAsync()
    {
        var result = await SendAsync<ModelListResponse>(HttpMethod.Get, "/api/models");
        return result?.Data.ToList() ?? [];
    }

    /// <summary>Obtém a configuração de conexões (somente admin).</summary>
    public Task<ConnectionsConfigResponse?> GetConnectionsAsync() =>
        SendAsync<ConnectionsConfigResponse>(HttpMethod.Get, "/api/v1/configs/connections");

    /// <summary>Atualiza a configuração de conexões (somente admin).</summary>
    public Task<ConnectionsConfigResponse?> UpdateConnectionsAsync(ConnectionsConfig config) =>
        SendAsync<ConnectionsConfigResponse>(HttpMethod.Post, "/api/v1/configs/connections", config);

    /// <summary>Obtém a versão do backend.</summary>
    public Task<VersionResponse?> GetVersionAsync() =>
        SendAsync<VersionResponse>(HttpMethod.Get, "/api/version");

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
}
