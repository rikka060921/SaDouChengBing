using System.Globalization;
using System.Text.Encodings.Web;
using ToDo.Razor.Utilities;

namespace ToDo.Test;

public class DocumentMarkdownPreviewTests
{
    [Fact]
    public void RendersDocumentStructureAndKeepsSourceMetadataOutOfTheBody()
    {
        var html = Render("""
            ---
            title: internal metadata
            ---

            # Requirements

            | Item | Status |
            | --- | --- |
            | Draft | Pending |

            - [x] **Review**

            ```text
            example
            ```

            [Go to requirements](#requirements)
            """);

        Assert.Contains("<h1 id=\"document-heading-requirements\">Requirements</h1>", html);
        Assert.Contains("<table>", html);
        Assert.Contains("<td>Draft</td>", html);
        Assert.Contains("<strong>Review</strong>", html);
        Assert.Contains("<pre><code", html);
        Assert.Contains("href=\"#document-heading-requirements\"", html);
        Assert.DoesNotContain("internal metadata", html);
    }

    [Fact]
    public void PreservesTextWhileBlockingUploadedHtmlAndAutomaticExternalRequests()
    {
        var html = Render("""
            <script>not markup in this preview</script>

            [Unsafe label](javascript:blocked)
            [Local reference](relative.md)
            ![Image description](https://example.invalid/image.png)
            [Safe link](https://example.invalid/docs)
            <https://example.invalid/auto>
            """);

        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("href=\"javascript:", html);
        Assert.DoesNotContain("href=\"relative.md\"", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("image.png", html);
        Assert.Contains("Unsafe label", html);
        Assert.Contains("Local reference", html);
        Assert.Contains("Image description", html);
        Assert.Contains("href=\"https://example.invalid/docs\"", html);
        Assert.Contains("href=\"https://example.invalid/auto\"", html);
        Assert.Contains("rel=\"noopener noreferrer\"", html);
    }

    private static string Render(string source)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        DocumentMarkdownPreview.Render(source).WriteTo(writer, HtmlEncoder.Default);
        return writer.ToString();
    }
}
