namespace OpenWebUI.Application.Contracts;

/// <summary>
/// Configuração de áudio server-side (admin). Engines suportadas:
/// STT <c>openai</c>, <c>deepgram</c> e <c>whisper</c> (servidor
/// faster-whisper externo, API OpenAI-compatível); TTS <c>openai</c>,
/// <c>transformers</c> (openedai-speech), <c>elevenlabs</c> e <c>azure</c>.
/// Providers embutidos no processo (whisper.cpp, kokoro) ficam fora de escopo.
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
    string? TtsVoice,
    string? AzureRegion)
{
    /// <summary>Config padrão — tudo desabilitado.</summary>
    public static readonly AudioConfig Default = new(
        "none", null, null, null, "none", null, null, null, null, null);

    /// <summary>Mascara as chaves para respostas GET.</summary>
    public AudioConfig Masked() => this with
    {
        SttApiKey = string.IsNullOrEmpty(SttApiKey) ? null : "********",
        TtsApiKey = string.IsNullOrEmpty(TtsApiKey) ? null : "********",
    };

    /// <summary>STT configurada (engine + base URL).</summary>
    public bool SttEnabled =>
        SttEngine is "openai" or "deepgram" or "whisper"
        && !string.IsNullOrWhiteSpace(SttBaseUrl);

    /// <summary>TTS configurado conforme os campos que cada engine exige.</summary>
    public bool TtsEnabled => TtsEngine switch
    {
        "openai" or "transformers" => !string.IsNullOrWhiteSpace(TtsBaseUrl),
        // ElevenLabs: base URL opcional (default api.elevenlabs.io), chave exigida.
        "elevenlabs" => !string.IsNullOrWhiteSpace(TtsApiKey),
        // Azure: região ou URL completa + chave de assinatura.
        "azure" => !string.IsNullOrWhiteSpace(TtsApiKey)
            && (!string.IsNullOrWhiteSpace(TtsBaseUrl)
                || !string.IsNullOrWhiteSpace(AzureRegion)),
        _ => false,
    };
}

/// <summary>Requisição de TTS.</summary>
public sealed record SpeechRequest(string Input, string? Voice, string? Model);

/// <summary>Resposta de STT.</summary>
public sealed record TranscriptionResponse(string Text);

/// <summary>Resultado de TTS: bytes e content-type do provider.</summary>
public sealed record SpeechResult(byte[] Audio, string ContentType);
