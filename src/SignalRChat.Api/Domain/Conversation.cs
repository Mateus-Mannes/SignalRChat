using Microsoft.AspNetCore.Identity;

namespace SignalRChat.Api.Domain;

public sealed class Conversation
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public IdentityUser CreatedByUser { get; set; } = null!;

    public ICollection<ConversationMember> Members { get; set; } = [];
}
