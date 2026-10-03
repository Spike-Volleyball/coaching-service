using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Coaching.Domain.Enums;

namespace Coaching.Application.RichText;

/// <summary>
/// A spreadsheet cell's lines as the editor HTML a coach would have written. A cell carries its
/// structure in the characters one can type into it: a dash or a bullet per point, "1." per step,
/// a heading ending in a colon over the points under it. Kept as plain text, all of that read as
/// one numbered list with the markers and headings in it as steps of their own (SPI-6502).
/// </summary>
/// <remarks>
/// The rules, in order: a marker is taken off its line and says which list the line is in, a
/// dash or bullet a bulleted one and a number a numbered one. A line ending in a colon holds the
/// run of marker lines under it when they are of another kind than itself. What is left of a run
/// of plain lines is listed in the chosen style, except a run of one, which is a paragraph — so a
/// cell holding one sentence stays that sentence.
/// </remarks>
public static class ImportedProse
{
    private static readonly Regex BulletMarker =
        new(@"^(?:[-*–](?:\s+|$)|[•◦]\s*)(?<text>.*)$", RegexOptions.Compiled);

    private static readonly Regex NumberMarker =
        new(@"^\d{1,3}[.)](?:\s+|$)(?<text>.*)$", RegexOptions.Compiled);

    /// <summary>How it runs: plain lines are numbered steps unless the import chose bullets.</summary>
    public static (string? Html, string[] Lines) Instructions(IEnumerable<string>? lines, DirectionsStyle style) =>
        Resolve(lines, ordered: style != DirectionsStyle.Bullets);

    /// <summary>Coaching points are a bulleted list, so their plain lines are bullets.</summary>
    public static (string? Html, string[] Lines) CoachingPoints(IEnumerable<string>? lines) =>
        Resolve(lines, ordered: false);

    /// <summary>Through the write funnel every drill takes, so both columns are stored alike.</summary>
    private static (string? Html, string[] Lines) Resolve(IEnumerable<string>? lines, bool ordered) =>
        DrillRichText.Resolve(ToHtml(Items(lines ?? []), ordered), null, ordered);

    private enum Kind
    {
        Plain,
        Bullet,
        Number
    }

    private sealed record Line(Kind Kind, string Text);

    /// <summary>A line and the points it heads, when it ends in a colon over a list.</summary>
    private sealed record Item(Line Line, IReadOnlyList<Line> Points);

    private static Line Read(string raw)
    {
        var line = DrillRichText.Collapse(raw);

        if (BulletMarker.Match(line) is { Success: true } bullet)
            return new Line(Kind.Bullet, bullet.Groups["text"].Value);

        return NumberMarker.Match(line) is { Success: true } number
            ? new Line(Kind.Number, number.Groups["text"].Value)
            : new Line(Kind.Plain, line);
    }

    private static List<Item> Items(IEnumerable<string> raw)
    {
        var lines = raw.Select(Read).Where(line => line.Text.Length > 0).ToList();
        var items = new List<Item>();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var points = new List<Line>();

            // A heading's points are the marker lines straight after it, all of one kind, and of
            // another kind than the heading: after "2. Variations:" the next "3." is a step of its
            // own, not a point under it.
            if (line.Text.EndsWith(':') && i + 1 < lines.Count
                && lines[i + 1].Kind is var pointKind && pointKind != Kind.Plain && pointKind != line.Kind)
            {
                while (i + 1 < lines.Count && lines[i + 1].Kind == pointKind)
                    points.Add(lines[++i]);
            }

            items.Add(new Item(line, points));
        }

        return items;
    }

    private static string? ToHtml(List<Item> items, bool ordered)
    {
        if (items.Count == 0) return null;

        var html = new StringBuilder();
        var start = 0;

        while (start < items.Count)
        {
            var kind = items[start].Line.Kind;
            var end = start;
            while (end + 1 < items.Count && items[end + 1].Line.Kind == kind)
                end++;

            var run = items.GetRange(start, end - start + 1);

            if (kind == Kind.Plain && run.Count == 1)
                AppendParagraph(html, run[0]);
            else
                AppendList(html, run, kind == Kind.Number || (kind == Kind.Plain && ordered));

            start = end + 1;
        }

        return html.ToString();
    }

    private static void AppendParagraph(StringBuilder html, Item item)
    {
        html.Append("<p>").Append(WebUtility.HtmlEncode(item.Line.Text)).Append("</p>");
        AppendPoints(html, item.Points);
    }

    private static void AppendList(StringBuilder html, IEnumerable<Item> items, bool ordered)
    {
        var tag = ordered ? "ol" : "ul";
        html.Append('<').Append(tag).Append('>');

        foreach (var item in items)
        {
            html.Append("<li><p>").Append(WebUtility.HtmlEncode(item.Line.Text)).Append("</p>");
            AppendPoints(html, item.Points);
            html.Append("</li>");
        }

        html.Append("</").Append(tag).Append('>');
    }

    private static void AppendPoints(StringBuilder html, IReadOnlyList<Line> points)
    {
        if (points.Count == 0) return;

        AppendList(html, points.Select(point => new Item(point, [])), points[0].Kind == Kind.Number);
    }
}
