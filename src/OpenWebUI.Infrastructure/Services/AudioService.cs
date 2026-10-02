using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// STT/TTS via providers HTTP: OpenAI-compatible (/audio/speech,
/// /audio/transcriptions, /audio/voices, /audio/models) e Deepgram (STT).
/// Sem provider configurado lança <see cref="AudioDisabledException"/> → 501.
/// Chaves ficam no servidor; o áudio do usuário não é persistido.
/// </summary>
public class AudioService(IHttpClientFactory httpFactory, ConfigService config)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Config ativa.</summary>
    /// <param name="ct">Cancelamento.</param>
    public Task<AudioConfig> GetConfigAsync(CancellationToken ct = default) =>
        config.GetAsync("audio.config", AudioConfig.Default, ct);

    /// <summary>Persiste a config.</summary>
    /// <param name="audio">Config a gravar.</param>
    /// <param name="ct">Cancelamento.</param>
    public Task SetConfigAsync(AudioConfig audio, CancellationToken ct = default) =>
        config.SetAsync("audio.config", audio, ct);

    /// <summary>
    /// Gera áudio a partir de texto no provider TTS configurado.
    /// </summary>
    /// <param name="input">Texto a sintetizar.</param>
    /// <param name="voice">Voz (null = config).</param>
    /// <param name="model">Modelo (null = config).</param>
    /// <param name="ct">Cancelamento.</param>
    /// <exception cref="AudioDisabledException">TTS desabilitado.</exception>
    /// <exception cref="AudioProviderException">Erro do provider.</exception>
    public async Task<SpeechResult> SpeechAsync(
        string input, string? voice, string? model, CancellationToken ct = default)
    {
        var cfg = await GetConfigAsync(ct);
        if (!cfg.TtsEnabled)
        {
            throw new AudioDisabledException("TTS desabilitado — configure um provider.");
        }

        var http = httpFactory.CreateClient(nameof(AudioService));
        http.Timeout = TimeSpan.FromSeconds(60);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{cfg.TtsBaseUrl!.TrimEnd('/')}/audio/speech");
        if (!string.IsNullOrEmpty(cfg.TtsApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.TtsApiKey);
        }
        request.Content = JsonContent.Create(new
        {
            model = model ?? cfg.TtsModel ?? "tts-1",
            voice = voice ?? cfg.TtsVoice ?? "alloy",
            input,
        }, options: JsonOptions);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new AudioProviderException(
                $"Provider TTS respondeu {(int)response.StatusCode}.");
        }

        return new SpeechResult(
            await response.Content.ReadAsByteArrayAsync(ct),
            response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg");
    }

    /// <summary>
    /// Transcreve um arquivo de áudio no provider STT configurado.
    /// </summary>
    /// <param name="stream">Conteúdo do arquivo.</param>
    /// <param name="fileName">Nome original (define formato).</param>
    /// <param name="ct">Cancelamento.</param>
    /// <exception cref="AudioDisabledException">STT desabilitada.</exception>
    /// <exception cref="AudioProviderException">Erro do provider.</exception>
    public async Task<string> TranscribeAsync(
        Stream stream, string fileName, CancellationToken ct = default)
    {
        var cfg = await GetConfigAsync(ct);
        if (!cfg.SttEnabled)
        {
            throw new AudioDisabledException("STT desabilitada — configure um provider.");
        }

        var http = httpFactory.CreateClient(nameof(AudioService));
        http.Timeout = TimeSpan.FromSeconds(120);
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(cfg.SttModel ?? "whisper-1"), "model");

        var url = cfg.SttEngine == "deepgram"
            ? $"{cfg.SttBaseUrl!.TrimEnd('/')}/v1/listen?model={cfg.SttModel ?? "nova-2"}"
            : $"{cfg.SttBaseUrl!.TrimEnd('/')}/audio/transcriptions";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrEmpty(cfg.SttApiKey))
        {
            request.Headers.Authorization = cfg.SttEngine == "deepgram"
                ? new AuthenticationHeaderValue("Token", cfg.SttApiKey)
                : new AuthenticationHeaderValue("Bearer", cfg.SttApiKey);
        }
        request.Content = cfg.SttEngine == "deepgram"
            ? new StreamContent(stream)
            : form;
        if (cfg.SttEngine == "deepgram")
        {
            request.Content.Headers.ContentType =
                new MediaTypeHeaderValue("application/octet-stream");
        }

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new AudioProviderException(
                $"Provider STT respondeu {(int)response.StatusCode}.");
        }

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        if (doc.TryGetProperty("text", out var text))
        {
            return text.GetString() ?? string.Empty;
        }
        // Deepgram: results.channels[0].alternatives[0].transcript
        if (doc.TryGetProperty("results", out var results)
            && results.TryGetProperty("channels", out var channels)
            && channels.GetArrayLength() > 0
            && channels[0].TryGetProperty("alternatives", out var alts)
            && alts.GetArrayLength() > 0)
        {
            return alts[0].GetProperty("transcript").GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    /// <summary>Lista vozes do provider TTS (GET /audio/voices).</summary>
    /// <param name="ct">Cancelamento.</param>
    public async Task<JsonElement> GetVoicesAsync(CancellationToken ct = default)
    {
        var cfg = await GetConfigAsync(ct);
        if (!cfg.TtsEnabled)
        {
            throw new AudioDisabledException("TTS desabilitado — configure um provider.");
        }

        var http = httpFactory.CreateClient(nameof(AudioService));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{cfg.TtsBaseUrl!.TrimEnd('/')}/audio/voices");
        if (!string.IsNullOrEmpty(cfg.TtsApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.TtsApiKey);
        }
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
    }

    /// <summary>Lista modelos do provider STT (GET /models — padrão OpenAI).</summary>
    /// <param name="ct">Cancelamento.</param>
    public async Task<JsonElement> GetModelsAsync(CancellationToken ct = default)
    {
        var cfg = await GetConfigAsync(ct);
        var baseUrl = cfg.SttEnabled ? cfg.SttBaseUrl
            : cfg.TtsEnabled ? cfg.TtsBaseUrl
            : null;
        var key = cfg.SttEnabled ? cfg.SttApiKey : cfg.TtsApiKey;
        if (baseUrl is null)
        {
            throw new AudioDisabledException("Áudio desabilitado — configure um provider.");
        }

        var http = httpFactory.CreateClient(nameof(AudioService));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{baseUrl.TrimEnd('/')}/models");
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
    }
}

/// <summary>Provider de áudio não configurado (endpoint → 501).</summary>
public class AudioDisabledException(string message) : Exception(message);

/// <summary>Falha no provider de áudio (endpoint → 502 com detail).</summary>
public class AudioProviderException(string message) : Exception(message);
