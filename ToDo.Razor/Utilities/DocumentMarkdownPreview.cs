using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Html;

namespace ToDo.Razor.Utilities;

public static class DocumentMarkdownPreview
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseYamlFrontMatter()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .Build();

    public static IHtmlContent Render(string content, string headingIdPrefix = "document-heading-")
    {
        var document = Markdown.Parse(content, Pipeline);
        foreach (var heading in document.Descendants<HeadingBlock>())
        {
            var attributes = heading.GetAttributes();
            if (!string.IsNullOrEmpty(attributes.Id))
                attributes.Id = headingIdPrefix + attributes.Id;
        }

        // The source is a project upload. Keep its words, but do not load images or allow active URL schemes.
        foreach (var inline in document.Descendants<Inline>().ToArray())
        {
            if (inline is LinkInline link)
            {
                if (link.IsImage || !SetSafeLink(link, link.GetDynamicUrl?.Invoke() ?? link.Url, headingIdPrefix))
                {
                    if (link.FirstChild == null && link.IsImage)
                        link.ReplaceBy(new LiteralInline("［图片］"), false);
                    else
                        link.ReplaceBy(new ContainerInline(), true);
                }
            }
            else if (inline is AutolinkInline autoLink)
            {
                var replacement = new LinkInline();
                if (SetSafeLink(replacement, autoLink.Url, headingIdPrefix))
                {
                    replacement.AppendChild(new LiteralInline(autoLink.Url));
                    autoLink.ReplaceBy(replacement, false);
                }
                else
                {
                    autoLink.ReplaceBy(new LiteralInline(autoLink.Url), false);
                }
            }
        }

        return new HtmlString(Markdown.ToHtml(document, Pipeline));
    }

    private static bool SetSafeLink(LinkInline link, string? url, string headingIdPrefix)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Any(char.IsControl))
            return false;

        if (url[0] == '#')
        {
            link.Url = "#" + headingIdPrefix + url[1..];
            link.GetDynamicUrl = null;
            return true;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return false;

        link.Url = uri.AbsoluteUri;
        link.GetDynamicUrl = null;
        var attributes = link.GetAttributes();
        attributes.AddProperty("target", "_blank");
        attributes.AddProperty("rel", "noopener noreferrer");
        return true;
    }
}
