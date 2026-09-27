using Shared.DTOs.Errors;
using Shared.Exceptions;

namespace Coaching.Application.Validation;

/// <summary>
/// A link a client supplies is stored as sent and later opened by a tap — an href on web, a
/// Linking.openURL on the phone — so only http and https are taken. javascript: runs script in the
/// reader's session, data: opens a page of the writer's choosing, and anything else hands the tap to
/// whichever app claims the scheme. Files uploaded to our bucket are https URLs, so they pass.
/// </summary>
public static class LinkUrl
{
    public const string NotHttpCode = "INVALID_URL";
    public const string NotHttpMessage = "A link must start with http:// or https://";

    public static bool IsHttp(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Refuses the request, naming every field whose link is not http or https. Called before a
    /// write stages anything, so a refused request leaves nothing behind.
    /// </summary>
    public static void EnsureHttp(IEnumerable<(string Field, string? Url)> links)
    {
        var errors = links
            .Where(link => !IsHttp(link.Url))
            .Select(link => new FieldError(link.Field, NotHttpCode, NotHttpMessage))
            .ToList();

        if (errors.Count > 0)
            throw new ValidationException(NotHttpMessage, errors);
    }
}
