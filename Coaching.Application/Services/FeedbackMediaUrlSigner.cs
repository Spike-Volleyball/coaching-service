using Amazon.S3;
using Amazon.S3.Model;
using Coaching.Application.Interfaces.Services;
using Microsoft.Extensions.Options;
using Shared.Options;

namespace Coaching.Application.Services;

/// <summary>
/// Feedback media is coach↔player scoped, so the `feedback/` S3 prefix is not
/// public-read (unlike profile pictures). Stored bare object URLs are converted
/// to presigned GET URLs at read time (SPI-5376); anything outside our bucket
/// (external links, legacy values) passes through untouched.
/// </summary>
public class FeedbackMediaUrlSigner(
    IAmazonS3 s3,
    IOptions<S3Settings> s3Settings,
    TimeProvider timeProvider) : IFeedbackMediaUrlSigner
{
    // Long enough that a video keeps streaming through ranged requests during a
    // session; clients refetch feedback on open, so URLs renew well before this.
    public static readonly TimeSpan ReadUrlLifetime = TimeSpan.FromHours(24);

    public string SignReadUrl(string url)
    {
        if (!IsStored(url)) return url;

        return s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = s3Settings.Value.Bucket,
            Key = KeyOf(url),
            Verb = HttpVerb.GET,
            Expires = timeProvider.GetUtcNow().UtcDateTime.Add(ReadUrlLifetime),
        });
    }

    public bool IsStored(string url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith(PublicBase, StringComparison.OrdinalIgnoreCase);

    public string ToStoredUrl(string url)
    {
        var sent = url.Trim();
        return IsStored(sent) ? PublicBase + KeyOf(sent) : sent;
    }

    private string PublicBase => s3Settings.Value.PublicBaseUrl.TrimEnd('/') + "/";

    /// <summary>The object key a URL into our bucket names, without any query a presign put on it.</summary>
    private string KeyOf(string storedUrl)
    {
        var key = storedUrl[PublicBase.Length..];
        var queryStart = key.IndexOf('?');
        return queryStart >= 0 ? key[..queryStart] : key;
    }
}
