using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using static Microsoft.Playwright.Assertions;

namespace SignalRChat.EndToEndTests;

public sealed class BaselineBrowserTests(AppHostFixture fixture)
    : BrowserTest, IClassFixture<AppHostFixture>
{
    [Fact]
    public async Task Two_authenticated_users_on_different_replicas_exchange_global_messages()
    {
        var affinities = await fixture.FindDistinctAffinitiesAsync();
        await using var firstContext = await CreateContextAsync(affinities.FirstAffinity);
        await using var secondContext = await CreateContextAsync(affinities.SecondAffinity);
        var firstPage = await firstContext.NewPageAsync();
        var secondPage = await secondContext.NewPageAsync();
        var firstEmail = $"browser-a-{Guid.NewGuid():N}@example.test";
        var secondEmail = $"browser-b-{Guid.NewGuid():N}@example.test";

        await RegisterAsync(firstPage, firstEmail);
        await RegisterAsync(secondPage, secondEmail);

        var firstInstance = await ReadCurrentInstanceAsync(firstPage);
        var secondInstance = await ReadCurrentInstanceAsync(secondPage);
        Assert.Equal(affinities.FirstInstance, firstInstance);
        Assert.Equal(affinities.SecondInstance, secondInstance);
        Assert.NotEqual(firstInstance, secondInstance);

        var firstMessage = $"hello-from-a-{Guid.NewGuid():N}";
        await firstPage.Locator("#messageInput").FillAsync(firstMessage);
        await firstPage.Locator("#sendButton").ClickAsync();
        await Expect(firstPage.GetByText($"{firstEmail} says {firstMessage}", new() { Exact = true }))
            .ToBeVisibleAsync();
        await Expect(secondPage.GetByText($"{firstEmail} says {firstMessage}", new() { Exact = true }))
            .ToBeVisibleAsync();

        var secondMessage = $"hello-from-b-{Guid.NewGuid():N}";
        await secondPage.Locator("#messageInput").FillAsync(secondMessage);
        await secondPage.Locator("#sendButton").ClickAsync();
        await Expect(firstPage.GetByText($"{secondEmail} says {secondMessage}", new() { Exact = true }))
            .ToBeVisibleAsync();
        await Expect(secondPage.GetByText($"{secondEmail} says {secondMessage}", new() { Exact = true }))
            .ToBeVisibleAsync();

        await firstPage.Locator("#logoutButton").ClickAsync();
        await secondPage.Locator("#logoutButton").ClickAsync();
        await Expect(firstPage.Locator("#authStatus")).ToHaveTextAsync("Not logged in");
        await Expect(secondPage.Locator("#authStatus")).ToHaveTextAsync("Not logged in");
    }

    [Fact]
    public async Task Owner_can_manage_a_conversation_and_member_can_leave_through_the_UI()
    {
        var affinities = await fixture.FindDistinctAffinitiesAsync();
        await using var ownerContext = await CreateContextAsync(affinities.FirstAffinity);
        await using var memberContext = await CreateContextAsync(affinities.SecondAffinity);
        var ownerPage = await ownerContext.NewPageAsync();
        var memberPage = await memberContext.NewPageAsync();
        var ownerEmail = $"browser-owner-{Guid.NewGuid():N}@example.test";
        var memberEmail = $"browser-member-{Guid.NewGuid():N}@example.test";
        var conversationName = $"Browser conversation {Guid.NewGuid():N}";

        await RegisterAsync(memberPage, memberEmail);
        await RegisterAsync(ownerPage, ownerEmail);

        await ownerPage.Locator("#conversationNameInput").FillAsync(conversationName);
        await ownerPage.Locator("#createConversationButton").ClickAsync();
        await Expect(ownerPage.Locator("#selectedConversationName")).ToHaveTextAsync(conversationName);

        await ownerPage.Locator("#memberEmailInput").FillAsync(memberEmail);
        await ownerPage.Locator("#addMemberButton").ClickAsync();
        await Expect(ownerPage.Locator("#membersList")).ToContainTextAsync(memberEmail);
        await Expect(ownerPage.Locator("#conversationStatus")).ToHaveTextAsync("Member added.");

        await memberPage.Locator("#refreshConversationsButton").ClickAsync();
        await Expect(memberPage.Locator("#selectedConversationName")).ToHaveTextAsync(conversationName);
        await Expect(memberPage.Locator("#leaveConversationButton")).ToBeVisibleAsync();

        await memberPage.Locator("#leaveConversationButton").ClickAsync();
        await Expect(memberPage.Locator("#conversationStatus"))
            .ToHaveTextAsync("You left the conversation.");
        await Expect(memberPage.Locator("#conversationSelect"))
            .ToHaveValueAsync(string.Empty);
    }

    private async Task<IBrowserContext> CreateContextAsync(string affinity)
    {
        var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = fixture.BaseAddress.ToString()
        });
        await context.AddCookiesAsync(
        [
            new Microsoft.Playwright.Cookie
            {
                Name = "signalr_affinity",
                Value = affinity,
                Url = fixture.BaseAddress.ToString(),
                SameSite = SameSiteAttribute.Lax
            }
        ]);
        return context;
    }

    private async Task RegisterAsync(IPage page, string email)
    {
        await page.GotoAsync("/");
        await page.Locator("#emailInput").FillAsync(email);
        await page.Locator("#passwordInput").FillAsync("Baseline-2026!Password");
        await page.Locator("#registerButton").ClickAsync();
        await Expect(page.Locator("#authStatus")).ToHaveTextAsync($"Logged in as {email}");
        await Expect(page.Locator("#sendButton")).ToBeEnabledAsync();
    }

    private static Task<string> ReadCurrentInstanceAsync(IPage page)
    {
        return page.EvaluateAsync<string>(
            """
            async () => {
                const response = await fetch('/account/me', { credentials: 'include' });
                if (!response.ok) {
                    throw new Error(`Account request failed with ${response.status}`);
                }

                return response.headers.get('X-SignalRChat-Instance');
            }
            """);
    }
}
