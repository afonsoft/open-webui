namespace OpenWebUI.Application.Contracts;

/// <summary>
/// Configuração de áudio server-side (admin). Engines suportadas:
/// <c>openai</c> (API compatível /audio/*) e <c>deepgram</c> (STT).
/// Providers locais (whisper.cpp, kokoro) ficam fora de escopo.
/// </summary>
public sealed record AudioConfig(
    string SttEngine,
    string? SttBaseUrl,
    string? SttApiKey,
    string? SttModel,
    string TtsEngine,
    string? TtsBaseUrl,
    string? TtsApiKey,
    string? TtsModel,
    string? TtsVoice)
{
    /// <summary>Config padrão — tudo desabilitado.</summary>
    public static readonly AudioConfig Default = new(
        "none", null, null, null, "none", null, null, null, null);

    /// <summary>Mascara as chaves para respostas GET.</summary>
    public AudioConfig Masked() => this with
    {
        SttApiKey = string.IsNullOrEmpty(SttApiKey) ? null : "********",
        TtsApiKey = string.IsNullOrEmpty(TtsApiKey) ? null : "********",
    };

    /// <summary>STT configurada (engine + base URL).</summary>
    public bool SttEnabled =>
        SttEngine is "openai" or "deepgram" && !string.IsNullOrWhiteSpace(SttBaseUrl);

    /// <summary>TTS configurado (engine + base URL).</summary>
    public bool TtsEnabled =>
        TtsEngine == "openai" && !string.IsNullOrWhiteSpace(TtsBaseUrl);
}

/// <summary>Requisição de TTS.</summary>
public sealed record SpeechRequest(string Input, string? Voice, string? Model);

/// <summary>Resposta de STT.</summary>
public sealed record TranscriptionResponse(string Text);

/// <summary>Resultado de TTS: bytes e content-type do provider.</summary>
public sealed record SpeechResult(byte[] Audio, string ContentType);
