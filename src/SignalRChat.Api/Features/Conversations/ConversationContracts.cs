namespace SignalRChat.Api.Features.Conversations;

public sealed record CreateConversationRequest(string? Name);

public sealed record AddConversationMemberRequest(string? Email);

public sealed record ConversationResponse(
    Guid Id,
    string Name,
    string CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    string CurrentUserRole,
    int ActiveMemberCount);

public sealed record ConversationListResponse(
    IReadOnlyList<ConversationResponse> Items,
    string? NextCursor);

public sealed record ConversationMemberResponse(
    string UserId,
    string Email,
    string Role,
    DateTimeOffset JoinedAtUtc);

public sealed record AddConversationMemberResponse(
    ConversationMemberResponse Member,
    bool Reactivated);
