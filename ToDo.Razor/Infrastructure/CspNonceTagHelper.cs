using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ToDo.Razor.Infrastructure;

[HtmlTargetElement("script")]
[HtmlTargetElement("style")]
public sealed class CspNonceTagHelper : TagHelper
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CspNonceTagHelper(IHttpContextAccessor httpContextAccessor)
        => _httpContextAccessor = httpContextAccessor;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (_httpContextAccessor.HttpContext?.Items["CspNonce"] is string nonce
            && !string.IsNullOrWhiteSpace(nonce))
            output.Attributes.SetAttribute("nonce", nonce);
    }
}
