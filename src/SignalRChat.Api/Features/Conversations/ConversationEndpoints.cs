using System.Security.Claims;

namespace SignalRChat.Api.Features.Conversations;

public static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var conversations = endpoints
            .MapGroup("/conversations")
            .RequireAuthorization();

        conversations.MapPost("", CreateAsync);
        conversations.MapGet("", ListAsync);
        conversations.MapGet("/{conversationId:guid}", GetAsync);
        conversations.MapGet("/{conversationId:guid}/members", ListMembersAsync);
        conversations.MapPost("/{conversationId:guid}/members", AddMemberAsync);
        conversations.MapDelete("/{conversationId:guid}/members/me", LeaveAsync);
        conversations.MapDelete("/{conversationId:guid}/members/{userId}", RemoveMemberAsync);

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        CreateConversationRequest request,
        ClaimsPrincipal user,
        ConversationService service,
        CancellationToken cancellationToken)
    {
        var conversation = await service.CreateAsync(
            GetUserId(user),
            request.Name,
            cancellationToken);

        return Results.Created($"/conversations/{conversation.Id}", conversation);
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user,
        ConversationService service,
        string? after = null,
        int limit = ConversationService.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var conversations = await service.ListAsync(
            GetUserId(user),
            after,
            limit,
            cancellationToken);

        return Results.Ok(conversations);
    }

    private static async Task<IResult> GetAsync(
        Guid conversationId,
        ClaimsPrincipal user,
        ConversationService service,
        CancellationToken cancellationToken)
    {
        return Results.Ok(await service.GetAsync(
            GetUserId(user),
            conversationId,
            cancellationToken));
    }

    private static async Task<IResult> ListMembersAsync(
        Guid conversationId,
        ClaimsPrincipal user,
        ConversationService service,
        CancellationToken cancellationToken)
    {
        return Results.Ok(await service.ListMembersAsync(
            GetUserId(user),
            conversationId,
            cancellationToken));
    }

    private static async Task<IResult> AddMemberAsync(
        Guid conversationId,
        AddConversationMemberRequest request,
        ClaimsPrincipal user,
        ConversationService service,
        CancellationToken cancellationToken)
    {
        var result = await service.AddMemberAsync(
            GetUserId(user),
            conversationId,
            request.Email,
            cancellationToken);

        return result.Reactivated
            ? Results.Ok(result)
            : Results.Created(
                $"/conversations/{conversationId}/members/{result.Member.UserId}",
                result);
    }

    private static async Task<IResult> RemoveMemberAsync(
        Guid conversationId,
        string userId,
        ClaimsPrincipal user,
        ConversationService service,
        CancellationToken cancellationToken)
    {
        await service.RemoveMemberAsync(
            GetUserId(user),
            conversationId,
            userId,
            cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> LeaveAsync(
        Guid conversationId,
        ClaimsPrincipal user,
        ConversationService service,
        CancellationToken cancellationToken)
    {
        await service.LeaveAsync(GetUserId(user), conversationId, cancellationToken);
        return Results.NoContent();
    }

    private static string GetUserId(ClaimsPrincipal user)
    {
        return user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ConversationProblemException(
                StatusCodes.Status401Unauthorized,
                "authenticated_user_missing",
                "The authenticated user identifier is missing.");
    }
}
