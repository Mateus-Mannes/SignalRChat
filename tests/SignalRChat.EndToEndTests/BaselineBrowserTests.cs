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
