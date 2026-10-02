namespace OpenWebUI.Application.Contracts;

/// <summary>Request body para <c>POST /api/v1/utils/code/format</c>.</summary>
public sealed record CodeFormatRequest(string Code, string? Language = null);
