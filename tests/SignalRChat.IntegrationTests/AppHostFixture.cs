using Aspire.Hosting;
using Aspire.Hosting.Testing;
using System.Net;

namespace SignalRChat.IntegrationTests;

public sealed class AppHostFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);
    private DistributedApplication? application;

    public Uri BaseAddress { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        using var cancellationSource = new CancellationTokenSource(StartupTimeout);
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.SignalRChat_AppHost>(
            [
                "SignalRChat:ReplicaCount=2",
                "SignalRChat:UsePersistentData=false",
                "SignalRChat:UseRandomPorts=true"
            ],
            cancellationSource.Token);

        application = await builder.BuildAsync(cancellationSource.Token);
        await application.StartAsync(cancellationSource.Token);

        foreach (var resourceName in new[]
                 {
                     "postgres",
                     "redis",
                     "signalr-api-1",
                     "signalr-api-2",
                     "web",
                     "nginx"
                 })
        {
            await application.ResourceNotifications.WaitForResourceHealthyAsync(
                resourceName,
                cancellationSource.Token);
        }

        using var client = application.CreateHttpClient("nginx", "http");
        BaseAddress = client.BaseAddress
            ?? throw new InvalidOperationException("The NGINX endpoint was not allocated.");
    }

    public HttpClient CreateClient(string affinity, CookieContainer? cookies = null)
    {
        cookies ??= new CookieContainer();
        cookies.Add(BaseAddress, new Cookie("signalr_affinity", affinity));

        return new HttpClient(new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true
        })
        {
            BaseAddress = BaseAddress
        };
    }

    public async Task<(string FirstAffinity, string FirstInstance, string SecondAffinity, string SecondInstance)>
        FindDistinctAffinitiesAsync(CancellationToken cancellationToken = default)
    {
        string? firstAffinity = null;
        string? firstInstance = null;

        for (var index = 0; index < 32; index++)
        {
            var affinity = $"baseline-affinity-{index}";
            using var client = CreateClient(affinity);
            using var response = await client.GetAsync("/account/me", cancellationToken);
            var instance = GetInstance(response);

            if (firstInstance is null)
            {
                firstAffinity = affinity;
                firstInstance = instance;
                continue;
            }

            if (!StringComparer.Ordinal.Equals(firstInstance, instance))
            {
                return (firstAffinity!, firstInstance, affinity, instance);
            }
        }

        throw new InvalidOperationException("Could not find affinity keys for two different API instances.");
    }

    public static string GetInstance(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues("X-SignalRChat-Instance", out var values)
            ? values.Single()
            : throw new InvalidOperationException("The API instance header was not present.");
    }

    public async Task DisposeAsync()
    {
        if (application is not null)
        {
            await application.DisposeAsync();
        }
    }
}
