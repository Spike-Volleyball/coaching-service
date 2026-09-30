using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace Coaching.Application.RichText;

/// <summary>
/// Drill prose (how it runs, coaching points) is authored in a rich text editor
/// and stored as HTML. Clients that predate the editor read flat string arrays,
/// so every write sanitizes the HTML and flattens it to lines for them. The HTML
/// is the source of truth; the arrays are derived and never written directly.
/// </summary>
public static class DrillRichText
{
    private const int MaxLines = 200;
    private const int MaxLineLength = 2000;

    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();
    private static readonly HtmlParser Parser = new();
    private static readonly System.Text.RegularExpressions.Regex Whitespace =
        new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        sanitizer.AllowedTags.UnionWith(new[] { "p", "br", "strong", "b", "em", "i", "u", "s", "a", "ul", "ol", "li" });
        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedAttributes.UnionWith(new[] { "href", "target", "rel" });
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.UnionWith(new[] { "http", "https" });
        return sanitizer;
    }

    /// <summary>Sanitized HTML, or null when the editor sent an empty document.</summary>
    public static string? Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var clean = Sanitizer.Sanitize(html);
        return HasText(clean) ? clean : null;
    }

    /// <summary>
    /// One line per block, matching what the editor shows. Keeps the legacy arrays
    /// in step with the HTML.
    /// </summary>
    public static string[] ToLines(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];

        var document = Parser.ParseDocument(html);
        if (document.Body is null) return [];

        return document.Body
            .QuerySelectorAll("li, p")
            // A list item's own paragraph would otherwise produce the line twice.
            .Where(element => element.TagName == "LI" || element.Closest("li") is null)
            .Select(element => Collapse(OwnText(element)))
            .Where(line => line.Length > 0)
            .Take(MaxLines)
            .ToArray();
    }

    /// <summary>
    /// A list item's words without its sub-list's: each nested item is a line of its own, so its
    /// text on the parent's line as well would glue the two together and say the child twice.
    /// </summary>
    private static string OwnText(IElement element) =>
        element.TagName == "LI"
            ? string.Concat(element.ChildNodes
                .Where(child => child is not IElement { TagName: "UL" or "OL" })
                .Select(child => child.TextContent))
            : element.TextContent;

    /// <summary>
    /// The single write funnel. HTML is the source of truth when the client sends it; otherwise
    /// it is built from the legacy arrays. Either way both columns are produced, so every reader
    /// sees the same content whichever shape it asks for.
    /// </summary>
    public static (string? Html, string[] Lines) Resolve(string? html, string[]? lines, bool ordered)
    {
        var sanitized = Sanitize(html);
        if (sanitized is not null)
            return (sanitized, ToLines(sanitized));

        var fallback = lines ?? [];
        return (FromLines(fallback, ordered), fallback);
    }

    /// <summary>Wraps legacy plain lines as list HTML, for backfilling rows written before the editor.</summary>
    public static string? FromLines(IReadOnlyCollection<string> lines, bool ordered)
    {
        var items = lines.Select(Collapse).Where(line => line.Length > 0).ToArray();
        if (items.Length == 0) return null;

        var tag = ordered ? "ol" : "ul";
        var body = string.Concat(items.Select(line => $"<li><p>{System.Net.WebUtility.HtmlEncode(line)}</p></li>"));
        return $"<{tag}>{body}</{tag}>";
    }

    private static bool HasText(string html)
    {
        var document = Parser.ParseDocument(html);
        return !string.IsNullOrWhiteSpace(document.Body?.TextContent);
    }

    private static string Collapse(string? text)
    {
        var collapsed = Whitespace.Replace(text ?? "", " ").Trim();
        return collapsed.Length > MaxLineLength ? collapsed[..MaxLineLength] : collapsed;
    }
}
