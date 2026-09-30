using Coaching.Application.RichText;
using Coaching.Domain.Enums;
using FluentAssertions;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.RichText;

/// <summary>
/// SPI-6502: a spreadsheet cell carries its structure in the characters a coach can type — a dash
/// per point, "1." per step, a heading ending in a colon over its points. The import keeps that
/// structure instead of numbering every line, markers and headings included.
/// </summary>
[TestFixture]
[Category("Unit")]
public class ImportedProseTests : UnitTestBase
{
    private const string VariationsHtml =
        "<ol><li><p>Split into 2 teams</p></li>" +
        "<li><p>Variations:</p><ul><li><p>Line</p></li><li><p>Sharp cross</p></li></ul></li></ol>";

    [TestCase(DirectionsStyle.Auto)]
    [TestCase(DirectionsStyle.Numbered)]
    public void Instructions_PlainLines_AreNumberedStepsExactlyAsImportsAlwaysWere(DirectionsStyle style)
    {
        string[] lines = ["Split into pairs", "Serve to zone one", "Rotate after 5"];

        var (html, stored) = ImportedProse.Instructions(lines, style);

        html.Should().Be(DrillRichText.FromLines(lines, ordered: true));
        stored.Should().Equal(lines);
    }

    [Test]
    public void Instructions_PlainLinesInTheBulletsStyle_AreBullets()
    {
        var (html, _) = ImportedProse.Instructions(["Split into pairs", "Serve to zone one"], DirectionsStyle.Bullets);

        html.Should().Be("<ul><li><p>Split into pairs</p></li><li><p>Serve to zone one</p></li></ul>");
    }

    [TestCase(DirectionsStyle.Auto)]
    [TestCase(DirectionsStyle.Numbered)]
    [TestCase(DirectionsStyle.Bullets)]
    public void Instructions_DashedLines_AreBulletsWithoutTheirDashes_WhateverTheStyle(DirectionsStyle style)
    {
        var (html, stored) = ImportedProse.Instructions(["- Split into pairs", "- Serve to zone one"], style);

        html.Should().Be("<ul><li><p>Split into pairs</p></li><li><p>Serve to zone one</p></li></ul>");
        stored.Should().Equal("Split into pairs", "Serve to zone one");
    }

    [TestCase("- ")]
    [TestCase("• ")]
    [TestCase("•")]
    [TestCase("* ")]
    [TestCase("– ")]
    [TestCase("◦ ")]
    [TestCase("-\t")]
    public void Instructions_EveryBulletACoachTypes_IsABullet(string marker)
    {
        var (html, _) = ImportedProse.Instructions([$"{marker}Line", $"{marker}Sharp cross"], DirectionsStyle.Numbered);

        html.Should().Be("<ul><li><p>Line</p></li><li><p>Sharp cross</p></li></ul>");
    }

    [TestCase("1. ", "2. ", DirectionsStyle.Auto)]
    [TestCase("1) ", "2) ", DirectionsStyle.Auto)]
    [TestCase("1. ", "2. ", DirectionsStyle.Bullets)]
    public void Instructions_NumberedLines_StayNumberedWithoutTheirNumbers_WhateverTheStyle(
        string first, string second, DirectionsStyle style)
    {
        var (html, stored) = ImportedProse.Instructions([$"{first}Split into pairs", $"{second}Serve to zone one"], style);

        html.Should().Be("<ol><li><p>Split into pairs</p></li><li><p>Serve to zone one</p></li></ol>");
        stored.Should().Equal("Split into pairs", "Serve to zone one");
    }

    [Test]
    public void Instructions_AHeadingEndingInAColon_HoldsTheDashedLinesUnderIt()
    {
        var (html, stored) = ImportedProse.Instructions(
            ["Split into 2 teams", "Variations:", "- Line", "- Sharp cross"], DirectionsStyle.Auto);

        html.Should().Be(VariationsHtml);
        stored.Should().Equal("Split into 2 teams", "Variations:", "Line", "Sharp cross");
    }

    [Test]
    public void Instructions_AHeadingOverItsPoints_InTheBulletsStyle_IsABulletHoldingThem()
    {
        var (html, _) = ImportedProse.Instructions(
            ["Split into 2 teams", "Variations:", "- Line", "- Sharp cross"], DirectionsStyle.Bullets);

        html.Should().Be(
            "<ul><li><p>Split into 2 teams</p></li>" +
            "<li><p>Variations:</p><ul><li><p>Line</p></li><li><p>Sharp cross</p></li></ul></li></ul>");
    }

    [Test]
    public void Instructions_AHeadingOverNumberedLines_HoldsANumberedList()
    {
        var (html, _) = ImportedProse.Instructions(
            ["Warm up in pairs", "Progression:", "1. Pass", "2. Set"], DirectionsStyle.Auto);

        html.Should().Be(
            "<ol><li><p>Warm up in pairs</p></li>" +
            "<li><p>Progression:</p><ol><li><p>Pass</p></li><li><p>Set</p></li></ol></li></ol>");
    }

    [Test]
    public void Instructions_ANumberedStepEndingInAColon_HoldsTheBulletsUnderIt_AndTheCountCarriesOn()
    {
        var (html, _) = ImportedProse.Instructions(
            ["1. Split into 2 teams", "2. Variations:", "- Line", "- Sharp cross", "3. Rotate"], DirectionsStyle.Auto);

        html.Should().Be(
            "<ol><li><p>Split into 2 teams</p></li>" +
            "<li><p>Variations:</p><ul><li><p>Line</p></li><li><p>Sharp cross</p></li></ul></li>" +
            "<li><p>Rotate</p></li></ol>");
    }

    [Test]
    public void Instructions_TheStepsCarryOnAfterAHeadingsPoints()
    {
        var (html, _) = ImportedProse.Instructions(
            ["Split into 2 teams", "Variations:", "- Line", "- Sharp cross", "Rotate after 5"], DirectionsStyle.Auto);

        html.Should().Be(
            "<ol><li><p>Split into 2 teams</p></li>" +
            "<li><p>Variations:</p><ul><li><p>Line</p></li><li><p>Sharp cross</p></li></ul></li>" +
            "<li><p>Rotate after 5</p></li></ol>");
    }

    [Test]
    public void Instructions_AHeadingWithNothingElseAroundIt_IsAParagraphOverItsList()
    {
        var (html, stored) = ImportedProse.Instructions(["Variations:", "- Line", "- Sharp cross"], DirectionsStyle.Auto);

        html.Should().Be("<p>Variations:</p><ul><li><p>Line</p></li><li><p>Sharp cross</p></li></ul>");
        stored.Should().Equal("Variations:", "Line", "Sharp cross");
    }

    [Test]
    public void Instructions_AColonOverPlainLines_IsJustAnotherStep()
    {
        var (html, _) = ImportedProse.Instructions(["Variations:", "Line", "Sharp cross"], DirectionsStyle.Auto);

        html.Should().Be(DrillRichText.FromLines(["Variations:", "Line", "Sharp cross"], ordered: true));
    }

    [Test]
    public void Instructions_AOneLineCell_IsAParagraph()
    {
        var (html, stored) = ImportedProse.Instructions(
            ["Coach tosses to the setter, who sets the outside hitter."], DirectionsStyle.Auto);

        html.Should().Be("<p>Coach tosses to the setter, who sets the outside hitter.</p>");
        stored.Should().Equal("Coach tosses to the setter, who sets the outside hitter.");
    }

    [Test]
    public void Instructions_AOneLineCellWrittenWithAMarker_StaysTheListItsMarkerAsksFor()
    {
        ImportedProse.Instructions(["- Serve to zone one"], DirectionsStyle.Auto).Html
            .Should().Be("<ul><li><p>Serve to zone one</p></li></ul>");
        ImportedProse.Instructions(["1. Serve to zone one"], DirectionsStyle.Bullets).Html
            .Should().Be("<ol><li><p>Serve to zone one</p></li></ol>");
    }

    [Test]
    public void Instructions_ALoneLineBetweenLists_IsAParagraph()
    {
        var (html, _) = ImportedProse.Instructions(
            ["Split into pairs", "- Line", "- Sharp cross", "Rotate after 5"], DirectionsStyle.Auto);

        html.Should().Be(
            "<p>Split into pairs</p><ul><li><p>Line</p></li><li><p>Sharp cross</p></li></ul><p>Rotate after 5</p>");
    }

    [Test]
    public void Instructions_TextThatOnlyLooksLikeAMarker_StaysText()
    {
        string[] lines = ["-5 points for a net touch", "*Serve* deep", "3 jump and in", "1.5 metres off the net"];

        var (html, stored) = ImportedProse.Instructions(lines, DirectionsStyle.Auto);

        html.Should().Be(DrillRichText.FromLines(lines, ordered: true));
        stored.Should().Equal(lines);
    }

    [Test]
    public void Instructions_EscapesMarkupInTheCell()
    {
        var (html, stored) = ImportedProse.Instructions(["- 5 < 6 & rising", "- <b>Serve</b>"], DirectionsStyle.Auto);

        html.Should().Be("<ul><li><p>5 &lt; 6 &amp; rising</p></li><li><p>&lt;b&gt;Serve&lt;/b&gt;</p></li></ul>");
        stored.Should().Equal("5 < 6 & rising", "<b>Serve</b>");
    }

    [Test]
    public void Instructions_CollapsesWhitespaceAndDropsBlankLines()
    {
        var (_, stored) = ImportedProse.Instructions(["  Split   into pairs ", "", "   ", "-   Serve"], DirectionsStyle.Auto);

        stored.Should().Equal("Split into pairs", "Serve");
    }

    [Test]
    public void Instructions_WithNothingToSay_IsEmpty()
    {
        // A marker with no words after it is an empty point, not a step reading "-".
        string[]?[] cells = [[], ["  ", "-", "1."], null];

        foreach (var cell in cells)
        {
            var (html, stored) = ImportedProse.Instructions(cell, DirectionsStyle.Auto);

            html.Should().BeNull();
            stored.Should().BeEmpty();
        }
    }

    [Test]
    public void CoachingPoints_PlainLines_AreBulletsExactlyAsImportsAlwaysWere()
    {
        string[] lines = ["Platform early", "Call the ball"];

        var (html, stored) = ImportedProse.CoachingPoints(lines);

        html.Should().Be(DrillRichText.FromLines(lines, ordered: false));
        stored.Should().Equal(lines);
    }

    [Test]
    public void CoachingPoints_GetTheSameMarkerTreatment()
    {
        var (html, stored) = ImportedProse.CoachingPoints(["Focus on:", "- a flat platform", "- calling early"]);

        html.Should().Be("<p>Focus on:</p><ul><li><p>a flat platform</p></li><li><p>calling early</p></li></ul>");
        stored.Should().Equal("Focus on:", "a flat platform", "calling early");
    }
}
