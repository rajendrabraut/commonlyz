using Markdig;
using Microsoft.AspNetCore.Components;

namespace Commonlyz.Core;

/// <summary>Markdown → HTML for user content. Raw HTML in the source is escaped, so output is safe to render.</summary>
public static class Markdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static MarkupString Render(string? markdown) =>
        new(Markdig.Markdown.ToHtml(markdown ?? "", Pipeline));
}
