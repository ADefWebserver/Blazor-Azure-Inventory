using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace BlazorAzureInventory.Components.Chat;

/// <summary>Renders assistant Markdown with raw HTML disabled and unsafe link schemes removed.</summary>
public static class MarkdownRenderer
{
    public const string ResourceScheme = "res:";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    /// <summary>
    /// <c>res:&lt;id&gt;</c> links become resource links (an encoded <c>data-resource-id</c> attribute) only when
    /// <paramref name="isKnownResource"/> confirms the ID is in the user's scan; other <c>res:</c> links become plain text.
    /// </summary>
    public static string ToSafeHtml(string markdown, Func<string, bool>? isKnownResource = null)
    {
        var document = Markdown.Parse(markdown ?? "", Pipeline);
        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (!link.IsImage && link.Url is { } url && url.StartsWith(ResourceScheme, StringComparison.OrdinalIgnoreCase))
            {
                var id = Uri.UnescapeDataString(url[ResourceScheme.Length..]).Trim();
                if (id.Length > 0 && isKnownResource?.Invoke(id) == true)
                {
                    link.Url = "#";
                    link.Title = null;
                    var attributes = link.GetAttributes();
                    attributes.AddClass("resource-link");
                    attributes.AddProperty("data-resource-id", id);
                    attributes.AddProperty("role", "button");
                }
                else
                {
                    link.ReplaceBy(new LiteralInline(PlainText(link)));
                }
                continue;
            }
            if (link.IsImage || !IsSafe(link.Url)) link.Url = "#";
        }
        foreach (var autolink in document.Descendants<AutolinkInline>())
        {
            if (!IsSafe(autolink.Url)) autolink.Url = "#";
        }
        return document.ToHtml(Pipeline);
    }

    private static string PlainText(ContainerInline container) =>
        string.Concat(container.Descendants<Inline>().Select(i => i switch
        {
            LiteralInline l => l.Content.ToString(),
            CodeInline c => c.Content,
            _ => ""
        }));

    private static bool IsSafe(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.StartsWith('#') || (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal))) return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeMailto);
    }
}
