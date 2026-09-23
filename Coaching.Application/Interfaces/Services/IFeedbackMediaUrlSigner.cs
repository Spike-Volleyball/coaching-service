namespace Coaching.Application.Interfaces.Services;

public interface IFeedbackMediaUrlSigner
{
    /// <summary>
    /// Returns a presigned GET URL for media stored in our bucket; any other
    /// URL (external links, empty values) is returned unchanged.
    /// </summary>
    string SignReadUrl(string url);

    /// <summary>
    /// Whether the URL points into our bucket — a file someone uploaded, rather than a link they
    /// pasted. Every upload lands there, so the stored URL alone answers it.
    /// </summary>
    bool IsStored(string url);

    /// <summary>
    /// The URL to store for one a client sent. A file in our bucket is kept as its bare object URL,
    /// whatever query arrived with it: an editor holds the presigned URL a read handed out, which
    /// expires, and storing that would take the player's file with it. Anything else is kept as sent.
    /// </summary>
    string ToStoredUrl(string url);
}
