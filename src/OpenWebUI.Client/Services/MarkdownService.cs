using Ganss.Xss;
using Markdig;

namespace OpenWebUI.Client.Services;

/// <summary>Renderiza Markdown em HTML sanitizado, seguro para exibição.</summary>
public class MarkdownService
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    /// <summary>Converte Markdown em HTML sanitizado.</summary>
    /// <param name="markdown">Texto em Markdown.</param>
    /// <returns>HTML seguro para injeção via MarkupString.</returns>
    public string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var html = Markdown.ToHtml(markdown, Pipeline);
        return Sanitizer.Sanitize(html);
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.AllowedAttributes.Add("id");
        return sanitizer;
    }
}
