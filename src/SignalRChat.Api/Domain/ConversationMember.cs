using Microsoft.AspNetCore.Identity;

namespace SignalRChat.Api.Domain;

public sealed class ConversationMember
{
    public Guid ConversationId { get; set; }

    public required string UserId { get; set; }

    public ConversationMemberRoleEnum Role { get; set; }

    public DateTimeOffset JoinedAtUtc { get; set; }

    public DateTimeOffset? LeftAtUtc { get; set; }

    public Conversation Conversation { get; set; } = null!;

    public IdentityUser User { get; set; } = null!;
}
