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
}
