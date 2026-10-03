using Microsoft.AspNetCore.Mvc;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Endpoints de áudio (/api/v1/audio): config admin mascarada, TTS /speech,
/// STT /transcriptions (multipart) e listagens /voices /models.
/// </summary>
public static class AudioEndpoints
{
    private const long MaxTranscriptionBytes = 25 * 1024 * 1024;
    private static readonly string[] AllowedExtensions =
        [".wav", ".mp3", ".webm", ".m4a", ".ogg", ".flac", ".mp4", ".mpeg", ".mpga"];

    /// <summary>Mapeia o grupo /api/v1/audio.</summary>
    public static void MapAudioEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/audio").RequireAuthorization();
        group.MapGet("/config", GetConfigAsync);
        group.MapGet("/capabilities", GetCapabilitiesAsync);
        group.MapPost("/config", UpdateConfigAsync);
        group.MapPost("/speech", SpeechAsync);
        group.MapPost("/transcriptions", TranscribeAsync).DisableAntiforgery();
        group.MapGet("/voices", GetVoicesAsync);
        group.MapGet("/models", GetModelsAsync);
    }

    private static async Task<IResult> GetConfigAsync(
        HttpContext http, AppDbContext db, AudioService audio, CancellationToken ct)
    {
        if (!await IsAdminAsync(http, db, ct))
        {
            return Results.Forbid();
        }

        return Results.Ok((await audio.GetConfigAsync(ct)).Masked());
    }

    /// <summary>Capacidades de áudio (auth) — o cliente usa para decidir o fallback.</summary>
    private static async Task<IResult> GetCapabilitiesAsync(
        HttpContext http, AppDbContext db, AudioService audio, CancellationToken ct)
    {
        if (await AuthEndpoints.FindUserAsync(http, db, ct) is null)
        {
            return Results.Unauthorized();
        }

        var cfg = await audio.GetResolvedConfigAsync(ct);
        return Results.Ok(new
        {
            stt = cfg.SttEnabled,
            tts = cfg.TtsEnabled,
            sttEngine = cfg.SttEnabled ? cfg.SttEngine : null,
            ttsEngine = cfg.TtsEnabled ? cfg.TtsEngine : null,
        });
    }

    private static async Task<IResult> UpdateConfigAsync(
        [FromBody] AudioConfig request,
        HttpContext http, AppDbContext db, AudioService audio, CancellationToken ct)
    {
        if (!await IsAdminAsync(http, db, ct))
        {
            return Results.Forbid();
        }

        var valid = request.SttEngine is "none" or "openai" or "deepgram" or "whisper"
                or "web-speech" or "provider"
            && request.TtsEngine is "none" or "openai" or "elevenlabs" or "azure"
                or "transformers" or "web-speech" or "provider";
        if (!valid)
        {
            return Results.BadRequest(new { detail = "Engine de áudio inválida." });
        }

        var current = await audio.GetConfigAsync(ct);
        var merged = request with
        {
            SttApiKey = request.SttApiKey is null or "********" ? current.SttApiKey : request.SttApiKey,
            TtsApiKey = request.TtsApiKey is null or "********" ? current.TtsApiKey : request.TtsApiKey,
        };
        await audio.SetConfigAsync(merged, ct);
        return Results.Ok(merged.Masked());
    }

    private static async Task<IResult> SpeechAsync(
        [FromBody] SpeechRequest request,
        HttpContext http, AppDbContext db, AudioService audio, CancellationToken ct)
    {
        if (await AuthEndpoints.FindUserAsync(http, db, ct) is null)
        {
            return Results.Unauthorized();
        }
        if (string.IsNullOrWhiteSpace(request.Input))
        {
            return Results.BadRequest(new { detail = "Input é obrigatório." });
        }

        try
        {
            var result = await audio.SpeechAsync(request.Input, request.Voice, request.Model, ct);
            return Results.File(result.Audio, result.ContentType);
        }
        catch (AudioDisabledException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status501NotImplemented);
        }
        catch (AudioProviderException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> TranscribeAsync(
        HttpContext http, AppDbContext db, AudioService audio, CancellationToken ct)
    {
        if (await AuthEndpoints.FindUserAsync(http, db, ct) is null)
        {
            return Results.Unauthorized();
        }
        if (!http.Request.HasFormContentType || http.Request.Form.Files.Count == 0)
        {
            return Results.BadRequest(new { detail = "Arquivo de áudio é obrigatório (multipart 'file')." });
        }

        var file = http.Request.Form.Files[0];
        if (file.Length == 0 || file.Length > MaxTranscriptionBytes)
        {
            return Results.BadRequest(new { detail = "Arquivo vazio ou acima de 25 MB." });
        }
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
        {
            return Results.BadRequest(new { detail = $"Formato não suportado: {ext}." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var text = await audio.TranscribeAsync(stream, file.FileName, ct);
            return Results.Ok(new TranscriptionResponse(text));
        }
        catch (AudioDisabledException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status501NotImplemented);
        }
        catch (AudioProviderException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> GetVoicesAsync(
        HttpContext http, AppDbContext db, AudioService audio, CancellationToken ct)
    {
        if (await AuthEndpoints.FindUserAsync(http, db, ct) is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            return Results.Ok(await audio.GetVoicesAsync(ct));
        }
        catch (AudioDisabledException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status501NotImplemented);
        }
        catch (HttpRequestException)
        {
            return Results.Problem("Provider indisponível.", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> GetModelsAsync(
        HttpContext http, AppDbContext db, AudioService audio, CancellationToken ct)
    {
        if (await AuthEndpoints.FindUserAsync(http, db, ct) is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            return Results.Ok(await audio.GetModelsAsync(ct));
        }
        catch (AudioDisabledException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status501NotImplemented);
        }
        catch (HttpRequestException)
        {
            return Results.Problem("Provider indisponível.", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<bool> IsAdminAsync(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        return user?.Role == UserRoles.Admin;
    }
}
