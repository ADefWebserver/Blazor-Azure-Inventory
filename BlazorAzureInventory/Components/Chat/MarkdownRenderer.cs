using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace BlazorAzureInventory.Components.Chat;

/// <summary>Renders assistant Markdown with raw HTML disabled and unsafe link schemes removed.</summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    public static string ToSafeHtml(string markdown)
    {
        var document = Markdown.Parse(markdown ?? "", Pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (link.IsImage || !IsSafe(link.Url)) link.Url = "#";
        }
        foreach (var autolink in document.Descendants<AutolinkInline>())
        {
            if (!IsSafe(autolink.Url)) autolink.Url = "#";
        }
        return document.ToHtml(Pipeline);
    }

    private static bool IsSafe(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.StartsWith('#') || (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal))) return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeMailto);
    }
}
