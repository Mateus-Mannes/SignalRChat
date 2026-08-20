using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SignalRChat.Api.Data;
using SignalRChat.Api.Domain;

namespace SignalRChat.Api.Features.Conversations;

public sealed class ConversationService(
    ApplicationDbContext dbContext,
    UserManager<IdentityUser> userManager,
    TimeProvider timeProvider)
{
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 100;
    public const int MaximumActiveMembers = 10;

    public async Task<ConversationResponse> CreateAsync(
        string userId,
        string? requestedName,
        CancellationToken cancellationToken)
    {
        var name = NormalizeName(requestedName);
        var now = timeProvider.GetUtcNow();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            Name = name,
            CreatedByUserId = userId,
            CreatedAtUtc = now,
            Members =
            [
                new ConversationMember
                {
                    UserId = userId,
                    Role = ConversationMemberRoleEnum.Owner,
                    JoinedAtUtc = now
                }
            ]
        };

        dbContext.Conversations.Add(conversation);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResponse(conversation, ConversationMemberRoleEnum.Owner, activeMemberCount: 1);
    }

    public async Task<ConversationListResponse> ListAsync(
        string userId,
        string? encodedCursor,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > MaximumPageSize)
        {
            throw Problem(
                StatusCodes.Status400BadRequest,
                "invalid_limit",
                "The page size is invalid.",
                $"Limit must be between 1 and {MaximumPageSize}.");
        }

        ConversationCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(encodedCursor))
        {
            if (!ConversationCursor.TryDecode(encodedCursor, out var decodedCursor))
            {
                throw Problem(
                    StatusCodes.Status400BadRequest,
                    "invalid_cursor",
                    "The conversation cursor is invalid.");
            }

            cursor = decodedCursor;
        }

        var query = ActiveConversationsFor(userId);

        if (cursor is { } position)
        {
            query = query.Where(item =>
                item.CreatedAtUtc < position.CreatedAtUtc
                || (item.CreatedAtUtc == position.CreatedAtUtc
                    && item.Id.CompareTo(position.Id) < 0));
        }

        var page = await query
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > limit;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        var items = page
            .Select(item => ToResponse(item))
            .ToArray();
        var nextCursor = hasMore
            ? new ConversationCursor(page[^1].CreatedAtUtc, page[^1].Id).Encode()
            : null;

        return new ConversationListResponse(items, nextCursor);
    }

    public async Task<ConversationResponse> GetAsync(
        string userId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = await ActiveConversationsFor(userId)
            .SingleOrDefaultAsync(item => item.Id == conversationId, cancellationToken);

        return conversation is null
            ? throw ConversationNotFound()
            : ToResponse(conversation);
    }

    public async Task<IReadOnlyList<ConversationMemberResponse>> ListMembersAsync(
        string userId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var canAccess = await dbContext.ConversationMembers.AnyAsync(
            member => member.ConversationId == conversationId
                      && member.UserId == userId
                      && member.LeftAtUtc == null,
            cancellationToken);

        if (!canAccess)
        {
            throw ConversationNotFound();
        }

        return await dbContext.ConversationMembers
            .AsNoTracking()
            .Where(member => member.ConversationId == conversationId && member.LeftAtUtc == null)
            .OrderBy(member => member.Role == ConversationMemberRoleEnum.Owner ? 0 : 1)
            .ThenBy(member => member.JoinedAtUtc)
            .ThenBy(member => member.UserId)
            .Select(member => new ConversationMemberResponse(
                member.UserId,
                member.User.Email ?? member.User.UserName ?? string.Empty,
                ToContractRole(member.Role),
                member.JoinedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<AddConversationMemberResponse> AddMemberAsync(
        string callerUserId,
        Guid conversationId,
        string? requestedEmail,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(requestedEmail);
        return await ExecuteMembershipMutationAsync(async () =>
        {
            await LockConversationAsync(conversationId, cancellationToken);

            var callerMembership = await GetActiveMembershipAsync(
                conversationId,
                callerUserId,
                cancellationToken);
            EnsureOwner(callerMembership);

            var targetUser = await userManager.FindByEmailAsync(email);

            if (targetUser is null)
            {
                throw Problem(
                    StatusCodes.Status404NotFound,
                    "user_not_found",
                    "The requested user was not found.");
            }

            var targetMembership = await dbContext.ConversationMembers.SingleOrDefaultAsync(
                member => member.ConversationId == conversationId && member.UserId == targetUser.Id,
                cancellationToken);

            if (targetMembership?.LeftAtUtc is null && targetMembership is not null)
            {
                throw Problem(
                    StatusCodes.Status409Conflict,
                    "member_already_active",
                    "The user is already an active conversation member.");
            }

            var activeMemberCount = await dbContext.ConversationMembers.CountAsync(
                member => member.ConversationId == conversationId && member.LeftAtUtc == null,
                cancellationToken);

            if (activeMemberCount >= MaximumActiveMembers)
            {
                throw Problem(
                    StatusCodes.Status409Conflict,
                    "member_limit_reached",
                    "The conversation already has the maximum number of active members.");
            }

            var now = timeProvider.GetUtcNow();
            var reactivated = targetMembership is not null;

            if (targetMembership is null)
            {
                targetMembership = new ConversationMember
                {
                    ConversationId = conversationId,
                    UserId = targetUser.Id,
                    Role = ConversationMemberRoleEnum.Member,
                    JoinedAtUtc = now
                };
                dbContext.ConversationMembers.Add(targetMembership);
            }
            else
            {
                targetMembership.Role = ConversationMemberRoleEnum.Member;
                targetMembership.JoinedAtUtc = now;
                targetMembership.LeftAtUtc = null;
            }

            return new AddConversationMemberResponse(
                new ConversationMemberResponse(
                    targetUser.Id,
                    targetUser.Email ?? targetUser.UserName ?? email,
                    ToContractRole(targetMembership.Role),
                    targetMembership.JoinedAtUtc),
                reactivated);
        }, cancellationToken);
    }

    public async Task RemoveMemberAsync(
        string callerUserId,
        Guid conversationId,
        string targetUserId,
        CancellationToken cancellationToken)
    {
        await ExecuteMembershipMutationAsync(async () =>
        {
            await LockConversationAsync(conversationId, cancellationToken);

            var callerMembership = await GetActiveMembershipAsync(
                conversationId,
                callerUserId,
                cancellationToken);
            EnsureOwner(callerMembership);

            var targetMembership = await GetActiveMembershipAsync(
                conversationId,
                targetUserId,
                cancellationToken);

            if (targetMembership is null)
            {
                throw Problem(
                    StatusCodes.Status404NotFound,
                    "member_not_found",
                    "The requested active member was not found.");
            }

            if (targetMembership.Role == ConversationMemberRoleEnum.Owner)
            {
                throw OwnerMembershipIsImmutable();
            }

            targetMembership.LeftAtUtc = timeProvider.GetUtcNow();
            return true;
        }, cancellationToken);
    }

    public async Task LeaveAsync(
        string userId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        await ExecuteMembershipMutationAsync(async () =>
        {
            await LockConversationAsync(conversationId, cancellationToken);

            var membership = await GetActiveMembershipAsync(
                conversationId,
                userId,
                cancellationToken);

            if (membership is null)
            {
                throw ConversationNotFound();
            }

            if (membership.Role == ConversationMemberRoleEnum.Owner)
            {
                throw OwnerMembershipIsImmutable();
            }

            membership.LeftAtUtc = timeProvider.GetUtcNow();
            return true;
        }, cancellationToken);
    }

    private IQueryable<ConversationData> ActiveConversationsFor(string userId)
    {
        return dbContext.ConversationMembers
            .AsNoTracking()
            .Where(membership => membership.UserId == userId && membership.LeftAtUtc == null)
            .Select(membership => new ConversationData
            {
                Id = membership.Conversation.Id,
                Name = membership.Conversation.Name,
                CreatedByUserId = membership.Conversation.CreatedByUserId,
                CreatedAtUtc = membership.Conversation.CreatedAtUtc,
                CurrentUserRole = membership.Role,
                ActiveMemberCount = membership.Conversation.Members.Count(
                    member => member.LeftAtUtc == null)
            });
    }

    private async Task<ConversationMember?> GetActiveMembershipAsync(
        Guid conversationId,
        string userId,
        CancellationToken cancellationToken)
    {
        return await dbContext.ConversationMembers.SingleOrDefaultAsync(
            member => member.ConversationId == conversationId
                      && member.UserId == userId
                      && member.LeftAtUtc == null,
            cancellationToken);
    }

    private async Task LockConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = await dbContext.Conversations
            .FromSqlInterpolated(
                $"SELECT * FROM \"Conversations\" WHERE \"Id\" = {conversationId} FOR UPDATE")
            .AsTracking()
            .SingleOrDefaultAsync(cancellationToken);

        if (conversation is null)
        {
            throw ConversationNotFound();
        }
    }

    private async Task<TResult> ExecuteMembershipMutationAsync<TResult>(
        Func<Task<TResult>> mutation,
        CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                cancellationToken);
            var result = await mutation();
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    private static void EnsureOwner(ConversationMember? membership)
    {
        if (membership is null)
        {
            throw ConversationNotFound();
        }

        if (membership.Role != ConversationMemberRoleEnum.Owner)
        {
            throw Problem(
                StatusCodes.Status403Forbidden,
                "not_conversation_owner",
                "Only the conversation owner can manage members.");
        }
    }

    private static string NormalizeName(string? requestedName)
    {
        var name = requestedName?.Trim();

        if (string.IsNullOrEmpty(name) || name.Length > 100)
        {
            throw Problem(
                StatusCodes.Status400BadRequest,
                "invalid_conversation_name",
                "The conversation name is invalid.",
                "Name must contain between 1 and 100 characters after trimming.");
        }

        return name;
    }

    private static string NormalizeEmail(string? requestedEmail)
    {
        var email = requestedEmail?.Trim();

        if (string.IsNullOrEmpty(email))
        {
            throw Problem(
                StatusCodes.Status400BadRequest,
                "invalid_member_email",
                "The member email is invalid.");
        }

        return email;
    }

    private static ConversationResponse ToResponse(
        Conversation conversation,
        ConversationMemberRoleEnum currentUserRole,
        int activeMemberCount)
    {
        return new ConversationResponse(
            conversation.Id,
            conversation.Name,
            conversation.CreatedByUserId,
            conversation.CreatedAtUtc,
            ToContractRole(currentUserRole),
            activeMemberCount);
    }

    private static ConversationResponse ToResponse(ConversationData conversation)
    {
        return new ConversationResponse(
            conversation.Id,
            conversation.Name,
            conversation.CreatedByUserId,
            conversation.CreatedAtUtc,
            ToContractRole(conversation.CurrentUserRole),
            conversation.ActiveMemberCount);
    }

    private static string ToContractRole(ConversationMemberRoleEnum role)
    {
        return role switch
        {
            ConversationMemberRoleEnum.Owner => "owner",
            ConversationMemberRoleEnum.Member => "member",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
        };
    }

    private static ConversationProblemException ConversationNotFound()
    {
        return Problem(
            StatusCodes.Status404NotFound,
            "conversation_not_found",
            "The conversation was not found.");
    }

    private static ConversationProblemException OwnerMembershipIsImmutable()
    {
        return Problem(
            StatusCodes.Status409Conflict,
            "owner_membership_immutable",
            "The conversation owner cannot leave or be removed.");
    }

    private static ConversationProblemException Problem(
        int statusCode,
        string code,
        string title,
        string? detail = null)
    {
        return new ConversationProblemException(statusCode, code, title, detail);
    }

    private sealed class ConversationData
    {
        public Guid Id { get; init; }

        public required string Name { get; init; }

        public required string CreatedByUserId { get; init; }

        public DateTimeOffset CreatedAtUtc { get; init; }

        public ConversationMemberRoleEnum CurrentUserRole { get; init; }

        public int ActiveMemberCount { get; init; }
    }
}
