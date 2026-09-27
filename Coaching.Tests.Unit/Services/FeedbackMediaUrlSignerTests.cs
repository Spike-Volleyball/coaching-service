using Amazon.S3;
using Amazon.S3.Model;
using Coaching.Application.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shared.Options;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class FeedbackMediaUrlSignerTests : UnitTestBase
{
    private const string PublicBaseUrl = "https://volleyer.s3.eu-west-2.amazonaws.com";

    private IAmazonS3 _s3 = null!;
    private FeedbackMediaUrlSigner _sut = null!;

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _s3 = Substitute.For<IAmazonS3>();
        _s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>())
            .Returns(call => $"https://signed.example/{call.Arg<GetPreSignedUrlRequest>().Key}?sig=abc");

        _sut = new FeedbackMediaUrlSigner(
            _s3,
            Options.Create(new S3Settings { Bucket = "volleyer", PublicBaseUrl = PublicBaseUrl }),
            TimeProvider);
    }

    [Test]
    public void SignReadUrl_OwnBucketUrl_ReturnsPresignedGet()
    {
        // Arrange
        var stored = $"{PublicBaseUrl}/feedback/coach-1/file-1.mp4";

        // Act
        var result = _sut.SignReadUrl(stored);

        // Assert
        result.Should().Be("https://signed.example/feedback/coach-1/file-1.mp4?sig=abc");
        _s3.Received(1).GetPreSignedURL(Arg.Is<GetPreSignedUrlRequest>(r =>
            r.BucketName == "volleyer" &&
            r.Key == "feedback/coach-1/file-1.mp4" &&
            r.Verb == HttpVerb.GET &&
            r.Expires == Now.Add(FeedbackMediaUrlSigner.ReadUrlLifetime)));
    }

    [Test]
    public void SignReadUrl_ExternalUrl_IsReturnedUntouched()
    {
        // Arrange
        var external = "https://www.youtube.com/watch?v=abc";

        // Act
        var result = _sut.SignReadUrl(external);

        // Assert
        result.Should().Be(external);
        _s3.DidNotReceive().GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>());
    }

    [Test]
    public void SignReadUrl_EmptyUrl_IsReturnedUntouched()
    {
        // Act & Assert
        _sut.SignReadUrl("").Should().Be("");
    }

    [Test]
    public void SignReadUrl_StripsQueryStringFromStoredUrl()
    {
        // Arrange
        var stored = $"{PublicBaseUrl}/feedback/coach-1/file-1.mp4?stale=token";

        // Act
        _sut.SignReadUrl(stored);

        // Assert
        _s3.Received(1).GetPreSignedURL(Arg.Is<GetPreSignedUrlRequest>(r =>
            r.Key == "feedback/coach-1/file-1.mp4"));
    }

    [Test]
    public void IsStored_OwnBucketUrl_IsTrue()
    {
        // Act & Assert
        _sut.IsStored($"{PublicBaseUrl}/feedback/coach-1/file-1.mp4").Should().BeTrue();
    }

    [Test]
    public void IsStored_ExternalUrl_IsFalse()
    {
        // Act & Assert
        _sut.IsStored("https://www.youtube.com/watch?v=abc").Should().BeFalse();
    }

    [Test]
    public void IsStored_UrlThatOnlyStartsLikeTheBucketHost_IsFalse()
    {
        // Arrange — the base is matched with its trailing slash, so a look-alike host is not ours.
        var lookalike = $"{PublicBaseUrl}.evil.example/feedback/file.mp4";

        // Act & Assert
        _sut.IsStored(lookalike).Should().BeFalse();
    }

    [Test]
    public void IsStored_EmptyUrl_IsFalse()
    {
        // Act & Assert
        _sut.IsStored("").Should().BeFalse();
    }

    [Test]
    public void ToStoredUrl_APresignedReadOfOurBucket_ReturnsTheBareObjectUrl()
    {
        // Arrange — what an editor holds after a read, and sends back when it re-adds a file.
        var presigned = $"{PublicBaseUrl}/feedback/coach-1/file-1.jpg" +
            "?X-Amz-Expires=86400&X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=AKIA%2F20260923&X-Amz-Signature=abc";

        // Act
        var result = _sut.ToStoredUrl(presigned);

        // Assert
        result.Should().Be($"{PublicBaseUrl}/feedback/coach-1/file-1.jpg");
    }

    [Test]
    public void ToStoredUrl_TheBareObjectUrlAnUploadReturned_IsKeptAsItIs()
    {
        // Arrange
        var uploaded = $"{PublicBaseUrl}/feedback/coach-1/file-1.jpg";

        // Act & Assert
        _sut.ToStoredUrl(uploaded).Should().Be(uploaded);
    }

    [Test]
    public void ToStoredUrl_OurBucketWrittenInAnotherCase_IsStoredUnderTheConfiguredBase()
    {
        // Act
        var result = _sut.ToStoredUrl($"{PublicBaseUrl.ToUpperInvariant()}/feedback/coach-1/file-1.jpg?X-Amz-Signature=abc");

        // Assert
        result.Should().Be($"{PublicBaseUrl}/feedback/coach-1/file-1.jpg");
    }

    [Test]
    public void ToStoredUrl_AnExternalLink_KeepsItsQuery()
    {
        // Arrange — a link's query is part of where it points.
        var external = "https://www.youtube.com/watch?v=abc&t=42";

        // Act & Assert
        _sut.ToStoredUrl(external).Should().Be(external);
    }

    [Test]
    public void ToStoredUrl_TrimsTheWhitespaceAroundAUrl()
    {
        // Act & Assert
        _sut.ToStoredUrl($"  {PublicBaseUrl}/feedback/coach-1/file-1.jpg  ").Should().Be($"{PublicBaseUrl}/feedback/coach-1/file-1.jpg");
        _sut.ToStoredUrl("  https://youtu.be/abc ").Should().Be("https://youtu.be/abc");
    }
}
