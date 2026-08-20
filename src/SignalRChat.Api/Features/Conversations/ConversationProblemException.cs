namespace SignalRChat.Api.Features.Conversations;

public sealed class ConversationProblemException(
    int statusCode,
    string code,
    string title,
    string? detail = null) : Exception(detail ?? title)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public string Title { get; } = title;

    public string? Detail { get; } = detail;
}
