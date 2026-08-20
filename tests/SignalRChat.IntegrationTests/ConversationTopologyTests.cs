using System.Net;
using System.Net.Http.Json;

namespace SignalRChat.IntegrationTests;

[Collection(AspireTopologyCollection.Name)]
public sealed class ConversationTopologyTests(AppHostFixture fixture)
{
    [Fact]
    public async Task Conversation_endpoints_require_authentication()
    {
        using var client = fixture.CreateClient("conversation-anonymous");

        using var listResponse = await client.GetAsync("/conversations");
        using var createResponse = await client.PostAsJsonAsync(
            "/conversations",
            new CreateConversationRequest("Private chat"));

        Assert.Equal(HttpStatusCode.Unauthorized, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, createResponse.StatusCode);
    }

    [Fact]
    public async Task Owner_can_create_list_and_retrieve_duplicate_named_conversations_with_cursor_pagination()
    {
        using var client = fixture.CreateClient("conversation-create-list");
        await RegisterAndLoginAsync(client);

        var first = await CreateConversationAsync(client, "  Project chat  ");
        var second = await CreateConversationAsync(client, "Project chat");
        var third = await CreateConversationAsync(client, "Another chat");

        Assert.Equal("Project chat", first.Name);
        Assert.Equal("owner", first.CurrentUserRole);
        Assert.Equal(1, first.ActiveMemberCount);
        Assert.NotEqual(first.Id, second.Id);

        using var firstPageResponse = await client.GetAsync("/conversations?limit=2");
        firstPageResponse.EnsureSuccessStatusCode();
        var firstPage = await ReadRequiredAsync<ConversationListResponse>(firstPageResponse);

        Assert.Equal(2, firstPage.Items.Count);
        Assert.False(string.IsNullOrWhiteSpace(firstPage.NextCursor));
        Assert.Equal(third.Id, firstPage.Items[0].Id);
        Assert.Equal(second.Id, firstPage.Items[1].Id);

        using var secondPageResponse = await client.GetAsync(
            $"/conversations?limit=2&after={Uri.EscapeDataString(firstPage.NextCursor!)}");
        secondPageResponse.EnsureSuccessStatusCode();
        var secondPage = await ReadRequiredAsync<ConversationListResponse>(secondPageResponse);

        Assert.Single(secondPage.Items);
        Assert.Equal(first.Id, secondPage.Items[0].Id);
        Assert.Null(secondPage.NextCursor);

        using var detailResponse = await client.GetAsync($"/conversations/{first.Id}");
        detailResponse.EnsureSuccessStatusCode();
        var detail = await ReadRequiredAsync<ConversationResponse>(detailResponse);

        Assert.Equal(first, detail);

        using var membersResponse = await client.GetAsync($"/conversations/{first.Id}/members");
        membersResponse.EnsureSuccessStatusCode();
        var members = await ReadRequiredAsync<List<ConversationMemberResponse>>(membersResponse);

        var owner = Assert.Single(members);
        Assert.Equal(first.CreatedByUserId, owner.UserId);
        Assert.Equal("owner", owner.Role);
    }

    [Fact]
    public async Task Membership_permissions_leave_remove_and_reactivation_are_enforced()
    {
        using var ownerClient = fixture.CreateClient("membership-owner");
        using var memberClient = fixture.CreateClient("membership-member");
        using var outsiderClient = fixture.CreateClient("membership-outsider");
        var owner = await RegisterAndLoginAsync(ownerClient);
        var member = await RegisterAndLoginAsync(memberClient);
        var outsider = await RegisterAndLoginAsync(outsiderClient);
        var conversation = await CreateConversationAsync(ownerClient, "Membership lifecycle");

        using var addResponse = await ownerClient.PostAsJsonAsync(
            $"/conversations/{conversation.Id}/members",
            new AddConversationMemberRequest(member.Email));
        Assert.Equal(HttpStatusCode.Created, addResponse.StatusCode);
        var added = await ReadRequiredAsync<AddConversationMemberResponse>(addResponse);
        Assert.False(added.Reactivated);
        Assert.Equal("member", added.Member.Role);

        using var duplicateResponse = await ownerClient.PostAsJsonAsync(
            $"/conversations/{conversation.Id}/members",
            new AddConversationMemberRequest(member.Email));
        await AssertProblemAsync(
            duplicateResponse,
            HttpStatusCode.Conflict,
            "member_already_active");

        using var memberDetailResponse = await memberClient.GetAsync($"/conversations/{conversation.Id}");
        Assert.Equal(HttpStatusCode.OK, memberDetailResponse.StatusCode);

        using var outsiderDetailResponse = await outsiderClient.GetAsync($"/conversations/{conversation.Id}");
        await AssertProblemAsync(
            outsiderDetailResponse,
            HttpStatusCode.NotFound,
            "conversation_not_found");

        using var memberManageResponse = await memberClient.PostAsJsonAsync(
            $"/conversations/{conversation.Id}/members",
            new AddConversationMemberRequest(outsider.Email));
        await AssertProblemAsync(
            memberManageResponse,
            HttpStatusCode.Forbidden,
            "not_conversation_owner");

        using var memberRemoveResponse = await memberClient.DeleteAsync(
            $"/conversations/{conversation.Id}/members/{conversation.CreatedByUserId}");
        await AssertProblemAsync(
            memberRemoveResponse,
            HttpStatusCode.Forbidden,
            "not_conversation_owner");

        using var ownerLeaveResponse = await ownerClient.DeleteAsync(
            $"/conversations/{conversation.Id}/members/me");
        await AssertProblemAsync(
            ownerLeaveResponse,
            HttpStatusCode.Conflict,
            "owner_membership_immutable");

        using var ownerRemoveSelfResponse = await ownerClient.DeleteAsync(
            $"/conversations/{conversation.Id}/members/{conversation.CreatedByUserId}");
        await AssertProblemAsync(
            ownerRemoveSelfResponse,
            HttpStatusCode.Conflict,
            "owner_membership_immutable");

        using var leaveResponse = await memberClient.DeleteAsync(
            $"/conversations/{conversation.Id}/members/me");
        Assert.Equal(HttpStatusCode.NoContent, leaveResponse.StatusCode);

        using var afterLeaveResponse = await memberClient.GetAsync($"/conversations/{conversation.Id}");
        Assert.Equal(HttpStatusCode.NotFound, afterLeaveResponse.StatusCode);

        using var listAfterLeaveResponse = await memberClient.GetAsync("/conversations");
        listAfterLeaveResponse.EnsureSuccessStatusCode();
        var listAfterLeave = await ReadRequiredAsync<ConversationListResponse>(listAfterLeaveResponse);
        Assert.DoesNotContain(listAfterLeave.Items, item => item.Id == conversation.Id);

        using var reactivateResponse = await ownerClient.PostAsJsonAsync(
            $"/conversations/{conversation.Id}/members",
            new AddConversationMemberRequest(member.Email));
        Assert.Equal(HttpStatusCode.OK, reactivateResponse.StatusCode);
        var reactivated = await ReadRequiredAsync<AddConversationMemberResponse>(reactivateResponse);
        Assert.True(reactivated.Reactivated);
        Assert.Equal(added.Member.UserId, reactivated.Member.UserId);

        using var removeResponse = await ownerClient.DeleteAsync(
            $"/conversations/{conversation.Id}/members/{reactivated.Member.UserId}");
        Assert.Equal(HttpStatusCode.NoContent, removeResponse.StatusCode);

        using var afterRemovalResponse = await memberClient.GetAsync($"/conversations/{conversation.Id}");
        Assert.Equal(HttpStatusCode.NotFound, afterRemovalResponse.StatusCode);

        using var secondReactivationResponse = await ownerClient.PostAsJsonAsync(
            $"/conversations/{conversation.Id}/members",
            new AddConversationMemberRequest(member.Email));
        Assert.Equal(HttpStatusCode.OK, secondReactivationResponse.StatusCode);

        Assert.NotEqual(owner.Email, member.Email);
    }

    [Fact]
    public async Task Concurrent_requests_on_different_API_instances_cannot_exceed_ten_active_members()
    {
        var ownerCookies = new CookieContainer();
        using var ownerClient = fixture.CreateClient("member-limit-owner", ownerCookies);
        await RegisterAndLoginAsync(ownerClient);
        var conversation = await CreateConversationAsync(ownerClient, "Ten member limit");
        using var registrationClient = fixture.CreateClient("member-limit-registration");
        var candidates = new List<Credentials>();

        for (var index = 0; index < 10; index++)
        {
            candidates.Add(await RegisterAsync(registrationClient));
        }

        foreach (var candidate in candidates.Take(8))
        {
            using var response = await ownerClient.PostAsJsonAsync(
                $"/conversations/{conversation.Id}/members",
                new AddConversationMemberRequest(candidate.Email));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var affinities = await fixture.FindDistinctAffinitiesAsync();
        using var firstClient = fixture.CreateClient(
            affinities.FirstAffinity,
            CopyAuthenticationCookies(ownerCookies));
        using var secondClient = fixture.CreateClient(
            affinities.SecondAffinity,
            CopyAuthenticationCookies(ownerCookies));

        var firstTask = firstClient.PostAsJsonAsync(
            $"/conversations/{conversation.Id}/members",
            new AddConversationMemberRequest(candidates[8].Email));
        var secondTask = secondClient.PostAsJsonAsync(
            $"/conversations/{conversation.Id}/members",
            new AddConversationMemberRequest(candidates[9].Email));

        var responses = await Task.WhenAll(firstTask, secondTask);
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];

        Assert.Equal(affinities.FirstInstance, AppHostFixture.GetInstance(firstResponse));
        Assert.Equal(affinities.SecondInstance, AppHostFixture.GetInstance(secondResponse));
        Assert.Equal(
            new[] { HttpStatusCode.Created, HttpStatusCode.Conflict },
            responses.Select(response => response.StatusCode).Order().ToArray());

        var conflict = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);
        var problem = await ReadRequiredAsync<ProblemResponse>(conflict);
        Assert.Equal("member_limit_reached", problem.Code);

        using var detailResponse = await ownerClient.GetAsync($"/conversations/{conversation.Id}");
        detailResponse.EnsureSuccessStatusCode();
        var detail = await ReadRequiredAsync<ConversationResponse>(detailResponse);
        Assert.Equal(10, detail.ActiveMemberCount);
    }

    private static async Task<ConversationResponse> CreateConversationAsync(
        HttpClient client,
        string name)
    {
        using var response = await client.PostAsJsonAsync(
            "/conversations",
            new CreateConversationRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadRequiredAsync<ConversationResponse>(response);
    }

    private static async Task<Credentials> RegisterAndLoginAsync(HttpClient client)
    {
        var credentials = await RegisterAsync(client);
        using var loginResponse = await client.PostAsJsonAsync(
            "/login?useCookies=true",
            credentials);
        loginResponse.EnsureSuccessStatusCode();
        return credentials;
    }

    private static async Task<Credentials> RegisterAsync(HttpClient client)
    {
        var credentials = new Credentials(
            $"conversation-{Guid.NewGuid():N}@example.test",
            "Conversation-2026!Password");
        using var registerResponse = await client.PostAsJsonAsync("/register", credentials);
        registerResponse.EnsureSuccessStatusCode();
        return credentials;
    }

    private CookieContainer CopyAuthenticationCookies(CookieContainer source)
    {
        var destination = new CookieContainer();

        foreach (Cookie cookie in source.GetCookies(fixture.BaseAddress))
        {
            if (!StringComparer.Ordinal.Equals(cookie.Name, "signalr_affinity"))
            {
                destination.Add(
                    fixture.BaseAddress,
                    new Cookie(cookie.Name, cookie.Value, cookie.Path));
            }
        }

        return destination;
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        var problem = await ReadRequiredAsync<ProblemResponse>(response);
        Assert.Equal(expectedCode, problem.Code);
    }

    private static async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response)
    {
        return await response.Content.ReadFromJsonAsync<T>()
            ?? throw new InvalidOperationException($"The response did not contain {typeof(T).Name}.");
    }

    private sealed record Credentials(string Email, string Password);

    private sealed record CreateConversationRequest(string Name);

    private sealed record AddConversationMemberRequest(string Email);

    private sealed record ConversationResponse(
        Guid Id,
        string Name,
        string CreatedByUserId,
        DateTimeOffset CreatedAtUtc,
        string CurrentUserRole,
        int ActiveMemberCount);

    private sealed record ConversationListResponse(
        IReadOnlyList<ConversationResponse> Items,
        string? NextCursor);

    private sealed record ConversationMemberResponse(
        string UserId,
        string Email,
        string Role,
        DateTimeOffset JoinedAtUtc);

    private sealed record AddConversationMemberResponse(
        ConversationMemberResponse Member,
        bool Reactivated);

    private sealed record ProblemResponse(string Code);
}
