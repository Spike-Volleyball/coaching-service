using System.Net;
using Shared.Enums;
using Shared.Exceptions;

namespace Coaching.Application.Exceptions;

/// <summary>
/// Drill and plan comments are written in social now. Reading them here still works for clients
/// that have not updated; writing them does not.
/// </summary>
public class CommentsMovedException()
    : ExceptionWithStatusAndErrorCodes(
        "Comments on drills and plans are written through the social service now. Update the app to comment.",
        HttpStatusCode.Gone,
        ErrorCodeEnum.CommentsMoved);
