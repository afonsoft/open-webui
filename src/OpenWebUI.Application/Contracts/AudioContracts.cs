namespace OpenWebUI.Application.Contracts;

/// <summary>
/// Configuração de áudio server-side (admin). Engines suportadas:
/// STT <c>openai</c>, <c>deepgram</c>, <c>whisper</c> (servidor
/// faster-whisper externo, API OpenAI-compatível) e <c>provider</c>
/// (conexão OpenAI cadastrada); TTS <c>openai</c>, <c>transformers</c>
/// (openedai-speech), <c>elevenlabs</c>, <c>azure</c> e <c>provider</c>.
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
    string? AzureRegion,
    string? SttProvider = null,
    string? TtsProvider = null)
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

    /// <summary>STT configurada (engine + base URL, ou conexão cadastrada).</summary>
    public bool SttEnabled =>
        (SttEngine is "openai" or "deepgram" or "whisper"
            && !string.IsNullOrWhiteSpace(SttBaseUrl))
        || (SttEngine == "provider" && !string.IsNullOrWhiteSpace(SttProvider));

    /// <summary>TTS configurado conforme os campos que cada engine exige.</summary>
    public bool TtsEnabled => TtsEngine switch
    {
        "openai" or "transformers" => !string.IsNullOrWhiteSpace(TtsBaseUrl),
        // Provider cadastrado: conexão OpenAI registrada nas conexões.
        "provider" => !string.IsNullOrWhiteSpace(TtsProvider),
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
