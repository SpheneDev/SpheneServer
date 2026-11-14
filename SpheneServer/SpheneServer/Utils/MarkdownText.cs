using System.Text;
using System.Text.RegularExpressions;

namespace SpheneServer.Utils;

/// <summary>
/// Markdown → plain text for server-generated excerpts (feed snippets,
/// notification snippets). The client renders bodies through Markdig; server
/// snippets only ever appear as single-line previews, so a conservative
/// strip is enough - emphasis markers, heading/list prefixes, Sphene inline
/// tags, links and images are removed while their text content survives.
/// </summary>
public static partial class MarkdownText
{
    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex ImageLink();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"</?(?:color(?:=[\w#]+)?|size(?:=\d{1,2})?|u)>")]
    private static partial Regex SpheneTag();

    [GeneratedRegex(@"\|\|([^|]*)\|\|")]
    private static partial Regex Spoiler();

    [GeneratedRegex(@"`{1,3}([^`]*)`{1,3}")]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"^#{1,6}\s+", RegexOptions.Multiline)]
    private static partial Regex HeadingPrefix();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+[.)])\s+", RegexOptions.Multiline)]
    private static partial Regex ListPrefix();

    [GeneratedRegex(@"\*\*([^*]+)\*\*|__([^_]+)__")]
    private static partial Regex Strong();

    [GeneratedRegex(@"\*([^*\n]+)\*|_([^_\n]+)_")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"~~([^~]+)~~")]
    private static partial Regex Strikethrough();

    [GeneratedRegex(@"^\s*(?:>{1,3}|-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.Multiline)]
    private static partial Regex BlockDivider();

    /// <summary>Flattens markdown to a single-line plain-text excerpt.</summary>
    public static string Strip(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;
        var text = markdown;
        text = ImageLink().Replace(text, string.Empty);
        text = Link().Replace(text, "$1");
        text = SpheneTag().Replace(text, string.Empty);
        text = Spoiler().Replace(text, "$1");
        text = CodeSpan().Replace(text, "$1");
        text = Strong().Replace(text, "$1$2");
        text = Emphasis().Replace(text, "$1$2");
        text = Strikethrough().Replace(text, "$1");
        text = HeadingPrefix().Replace(text, string.Empty);
        text = ListPrefix().Replace(text, string.Empty);
        text = BlockDivider().Replace(text, " ");
        return Whitespace().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
