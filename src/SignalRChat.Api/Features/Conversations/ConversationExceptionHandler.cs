using Microsoft.AspNetCore.Diagnostics;

namespace SignalRChat.Api.Features.Conversations;

public sealed class ConversationExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ConversationProblemException problem)
        {
            return false;
        }

        await Results.Problem(
                statusCode: problem.StatusCode,
                title: problem.Title,
                detail: problem.Detail,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = problem.Code
                })
            .ExecuteAsync(httpContext);

        return true;
    }
}
