using Coaching.Application.RichText;
using FluentAssertions;

namespace Coaching.Tests.Unit.RichText;

[TestFixture]
[Category("Unit")]
public class DrillRichTextTests
{
    [Test]
    public void Sanitize_StripsScriptsAndKeepsFormatting()
    {
        const string hostile = "<ol><li><p>Serve <strong>deep</strong><script>alert(1)</script></p></li></ol>";

        var result = DrillRichText.Sanitize(hostile);

        result.Should().NotContain("script");
        result.Should().Contain("<strong>deep</strong>");
    }

    [Test]
    public void Sanitize_DropsJavascriptHrefButKeepsTheText()
    {
        var result = DrillRichText.Sanitize("<p><a href=\"javascript:alert(1)\">tap</a></p>");

        result.Should().NotContain("javascript:");
        result.Should().Contain("tap");
    }

    [Test]
    public void Sanitize_TreatsAnEmptyEditorDocumentAsNull()
    {
        // What the editor sends when a coach opens the field and types nothing.
        DrillRichText.Sanitize("<ol><li><p></p></li></ol>").Should().BeNull();
        DrillRichText.Sanitize("   ").Should().BeNull();
        DrillRichText.Sanitize(null).Should().BeNull();
    }

    [Test]
    public void ToLines_GivesOneLinePerListItem()
    {
        const string html = "<ol><li><p>First</p></li><li><p>Second</p></li></ol>";

        DrillRichText.ToLines(html).Should().Equal("First", "Second");
    }

    [Test]
    public void ToLines_DoesNotDuplicateAListItemsOwnParagraph()
    {
        // li and p both match the selector; the item must still yield a single line.
        DrillRichText.ToLines("<ul><li><p>Only once</p></li></ul>").Should().Equal("Only once");
    }

    [Test]
    public void ToLines_GivesANestedItemItsOwnLine_AndLeavesItOutOfItsParents()
    {
        // SPI-6499: the parent's text used to carry its children's glued on, and each child then
        // came a second time.
        const string html = "<ul><li><p>Parent:</p><ul><li><p>Child</p></li></ul></li></ul>";

        DrillRichText.ToLines(html).Should().Equal("Parent:", "Child");
    }

    [Test]
    public void ToLines_ReadsAHeadingOverItsPointsInsideNumberedSteps()
    {
        const string html =
            "<ol><li><p>Split into 2 teams</p></li>" +
            "<li><p>Variations:</p><ul><li><p>Line</p></li><li><p>Sharp cross</p></li></ul></li></ol>";

        DrillRichText.ToLines(html).Should().Equal("Split into 2 teams", "Variations:", "Line", "Sharp cross");
    }

    [Test]
    public void ToLines_KeepsTheFormattedWordsOfAnItemThatHasASubList()
    {
        const string html = "<ul><li><p>Serve <strong>deep</strong> first:</p><ol><li><p>Zone one</p></li></ol></li></ul>";

        DrillRichText.ToLines(html).Should().Equal("Serve deep first:", "Zone one");
    }

    [Test]
    public void Resolve_StoresTheLinesOfANestedListOnceEach()
    {
        // The drill "Sheet Defense" as the web editor saved it on 2026-09-24.
        const string html =
            "<ul><li><p>Variations:</p><ul><li><p>have coaches or players hold the sheet</p></li></ul></li></ul>";

        var (_, lines) = DrillRichText.Resolve(html, null, ordered: true);

        lines.Should().Equal("Variations:", "have coaches or players hold the sheet");
    }

    [Test]
    public void ToLines_SkipsBlankBlocks()
    {
        DrillRichText.ToLines("<ol><li><p>Kept</p></li><li><p>  </p></li></ol>").Should().Equal("Kept");
    }

    [Test]
    public void FromLines_WrapsLegacyLinesAsAList()
    {
        var html = DrillRichText.FromLines(["First", "Second"], ordered: true);

        html.Should().Be("<ol><li><p>First</p></li><li><p>Second</p></li></ol>");
    }

    [Test]
    public void FromLines_EscapesMarkupInStoredText()
    {
        var html = DrillRichText.FromLines(["5 < 6 & rising"], ordered: false);

        html.Should().Be("<ul><li><p>5 &lt; 6 &amp; rising</p></li></ul>");
    }

    [Test]
    public void FromLines_ReturnsNullWhenThereIsNothingToWrap()
    {
        DrillRichText.FromLines([], ordered: true).Should().BeNull();
        DrillRichText.FromLines(["   "], ordered: true).Should().BeNull();
    }

    [Test]
    public void RoundTrip_LegacyLinesSurviveBothDirections()
    {
        string[] original = ["Form two groups", "Serve to zones"];

        var lines = DrillRichText.ToLines(DrillRichText.FromLines(original, ordered: true));

        lines.Should().Equal(original);
    }
}
