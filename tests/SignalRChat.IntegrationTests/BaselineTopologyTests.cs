using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace SignalRChat.IntegrationTests;

public sealed class BaselineTopologyTests(AppHostFixture fixture) : IClassFixture<AppHostFixture>
{
    [Fact]
    public async Task Nginx_serves_the_web_application()
    {
        using var client = fixture.CreateClient("web-smoke-test");

        using var healthResponse = await client.GetAsync("/nginx-health");
        using var webResponse = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, webResponse.StatusCode);
    }

    [Fact]
    public async Task Authentication_endpoints_register_login_and_logout_a_user()
    {
        using var client = fixture.CreateClient("authentication-test");
        var credentials = CreateCredentials();

        using var anonymousResponse = await client.GetAsync("/account/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var registerResponse = await client.PostAsJsonAsync("/register", credentials);
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);

        using var loginResponse = await client.PostAsJsonAsync("/login?useCookies=true", credentials);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        using var accountResponse = await client.GetAsync("/account/me");
        Assert.Equal(HttpStatusCode.OK, accountResponse.StatusCode);
        var account = await accountResponse.Content.ReadFromJsonAsync<AccountResponse>();
        Assert.Equal(credentials.Email, account?.Email);
        Assert.False(string.IsNullOrWhiteSpace(account?.Instance));

        using var logoutResponse = await client.PostAsync("/logout", content: null);
        Assert.Equal(HttpStatusCode.OK, logoutResponse.StatusCode);

        using var loggedOutResponse = await client.GetAsync("/account/me");
        Assert.Equal(HttpStatusCode.Unauthorized, loggedOutResponse.StatusCode);
    }

    [Fact]
    public async Task Nginx_keeps_an_affinity_cookie_on_the_same_API_instance()
    {
        using var client = fixture.CreateClient("sticky-session-test");
        var instances = new List<string>();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await client.GetAsync("/account/me");
            instances.Add(AppHostFixture.GetInstance(response));
        }

        Assert.Single(instances.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Authentication_cookie_is_accepted_by_a_different_API_instance()
    {
        var affinities = await fixture.FindDistinctAffinitiesAsync();
        var sourceCookies = new CookieContainer();
        using var sourceClient = fixture.CreateClient(affinities.FirstAffinity, sourceCookies);
        var credentials = CreateCredentials();

        using var registerResponse = await sourceClient.PostAsJsonAsync("/register", credentials);
        registerResponse.EnsureSuccessStatusCode();
        using var loginResponse = await sourceClient.PostAsJsonAsync("/login?useCookies=true", credentials);
        loginResponse.EnsureSuccessStatusCode();

        var destinationCookies = CopyAuthenticationCookies(sourceCookies, fixture.BaseAddress);
        using var destinationClient = fixture.CreateClient(affinities.SecondAffinity, destinationCookies);
        using var accountResponse = await destinationClient.GetAsync("/account/me");

        Assert.Equal(HttpStatusCode.OK, accountResponse.StatusCode);
        Assert.Equal(affinities.SecondInstance, AppHostFixture.GetInstance(accountResponse));
        var account = await accountResponse.Content.ReadFromJsonAsync<AccountResponse>();
        Assert.Equal(credentials.Email, account?.Email);
    }

    [Fact]
    public async Task SignalR_negotiation_offers_all_supported_transports()
    {
        var cookies = new CookieContainer();
        using var client = fixture.CreateClient("negotiation-test", cookies);
        await RegisterAndLoginAsync(client);

        using var response = await client.PostAsync("/chatHub/negotiate?negotiateVersion=1", content: null);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var transports = document.RootElement
            .GetProperty("availableTransports")
            .EnumerateArray()
            .Select(item => item.GetProperty("transport").GetString())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("WebSockets", transports);
        Assert.Contains("ServerSentEvents", transports);
        Assert.Contains("LongPolling", transports);
    }

    [Theory]
    [InlineData(HttpTransportType.WebSockets)]
    [InlineData(HttpTransportType.ServerSentEvents)]
    [InlineData(HttpTransportType.LongPolling)]
    public async Task SignalR_connects_with_each_supported_transport(HttpTransportType transport)
    {
        var cookies = new CookieContainer();
        using var client = fixture.CreateClient($"transport-{transport}", cookies);
        await RegisterAndLoginAsync(client);
        await using var connection = CreateHubConnection(cookies, transport);

        await connection.StartAsync();

        Assert.Equal(HubConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task Redis_backplane_delivers_a_global_message_across_API_instances()
    {
        var affinities = await fixture.FindDistinctAffinitiesAsync();
        var firstCookies = new CookieContainer();
        var secondCookies = new CookieContainer();
        using var firstClient = fixture.CreateClient(affinities.FirstAffinity, firstCookies);
        using var secondClient = fixture.CreateClient(affinities.SecondAffinity, secondCookies);
        var firstCredentials = await RegisterAndLoginAsync(firstClient);
        await RegisterAndLoginAsync(secondClient);
        await using var firstConnection = CreateHubConnection(firstCookies);
        await using var secondConnection = CreateHubConnection(secondCookies);
        var messageReceived = new TaskCompletionSource<(string User, string Message)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = secondConnection.On<string, string>(
            "ReceiveMessage",
            (user, message) => messageReceived.TrySetResult((user, message)));

        await firstConnection.StartAsync();
        await secondConnection.StartAsync();

        var message = $"cross-replica-{Guid.NewGuid():N}";
        await firstConnection.InvokeAsync("SendMessage", message);
        var received = await messageReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(firstCredentials.Email, received.User);
        Assert.Equal(message, received.Message);
    }

    private HubConnection CreateHubConnection(
        CookieContainer cookies,
        HttpTransportType transports = HttpTransportType.WebSockets |
                                       HttpTransportType.ServerSentEvents |
                                       HttpTransportType.LongPolling)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(fixture.BaseAddress, "/chatHub"), options =>
            {
                options.Cookies = cookies;
                options.Transports = transports;
            })
            .Build();
    }

    private static CookieContainer CopyAuthenticationCookies(CookieContainer source, Uri baseAddress)
    {
        var destination = new CookieContainer();

        foreach (Cookie cookie in source.GetCookies(baseAddress))
        {
            if (!StringComparer.Ordinal.Equals(cookie.Name, "signalr_affinity"))
            {
                destination.Add(baseAddress, new Cookie(cookie.Name, cookie.Value, cookie.Path));
            }
        }

        return destination;
    }

    private static Credentials CreateCredentials()
    {
        return new Credentials(
            $"baseline-{Guid.NewGuid():N}@example.test",
            "Baseline-2026!Password");
    }

    private static async Task<Credentials> RegisterAndLoginAsync(HttpClient client)
    {
        var credentials = CreateCredentials();
        using var registerResponse = await client.PostAsJsonAsync("/register", credentials);
        registerResponse.EnsureSuccessStatusCode();
        using var loginResponse = await client.PostAsJsonAsync("/login?useCookies=true", credentials);
        loginResponse.EnsureSuccessStatusCode();
        return credentials;
    }

    private sealed record Credentials(string Email, string Password);
    private sealed record AccountResponse(string Email, string Instance);
}
