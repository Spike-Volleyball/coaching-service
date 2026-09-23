using Coaching.Application.Validation;
using FluentAssertions;
using Shared.Exceptions;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Validation;

/// <summary>
/// A link a client supplies is stored as given and later opened by a tap — an href on web, a
/// Linking.openURL on the phone — so anything but http and https could run script or hand the tap
/// to an unexpected handler (SPI-6458).
/// </summary>
[TestFixture]
[Category("Unit")]
public class LinkUrlTests : UnitTestBase
{
    [TestCase("https://youtu.be/serve")]
    [TestCase("http://example.com/drill.pdf")]
    [TestCase("HTTPS://VIMEO.COM/123")]
    [TestCase("https://volleyer.s3.eu-west-2.amazonaws.com/feedback/c/f.jpg?X-Amz-Signature=abc")]
    [TestCase("  https://youtu.be/serve  ")]
    public void IsHttp_WithAnHttpOrHttpsUrl_ReturnsTrue(string url)
    {
        // Act
        var result = LinkUrl.IsHttp(url);

        // Assert
        result.Should().BeTrue();
    }

    [TestCase("javascript:alert(1)")]
    [TestCase("JavaScript:alert(document.cookie)")]
    [TestCase("  javascript:alert(1)")]
    [TestCase("java\tscript:alert(1)")]
    [TestCase("data:text/html,<script>alert(1)</script>")]
    [TestCase("vbscript:msgbox(1)")]
    [TestCase("file:///etc/passwd")]
    [TestCase("intent://scan/#Intent;scheme=zxing;end")]
    [TestCase("mailto:coach@example.com")]
    [TestCase("youtube.com/watch?v=serve")]
    [TestCase("/relative/path")]
    [TestCase("//evil.example.com/x")]
    [TestCase("https://")]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void IsHttp_WithAnythingElse_ReturnsFalse(string? url)
    {
        // Act
        var result = LinkUrl.IsHttp(url);

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public void EnsureHttp_WhenEveryLinkIsHttp_DoesNotThrow()
    {
        // Arrange
        var links = new (string, string?)[] { ("attachments[0].url", "https://youtu.be/serve"), ("url", "http://example.com") };

        // Act
        var act = () => LinkUrl.EnsureHttp(links);

        // Assert
        act.Should().NotThrow();
    }

    [Test]
    public void EnsureHttp_WithBadLinks_NamesEveryFieldThatFailed()
    {
        // Arrange
        var links = new (string, string?)[]
        {
            ("attachments[0].url", "https://youtu.be/serve"),
            ("attachments[1].url", "javascript:alert(1)"),
            ("improvementPoints[0].mediaLinks[0].url", "data:text/html,x"),
        };

        // Act
        var act = () => LinkUrl.EnsureHttp(links);

        // Assert
        var error = act.Should().Throw<ValidationException>().Which;
        error.FieldErrors.Select(e => e.Field)
            .Should().Equal("attachments[1].url", "improvementPoints[0].mediaLinks[0].url");
        error.FieldErrors.Should().OnlyContain(e => e.Code == LinkUrl.NotHttpCode);
    }
}
