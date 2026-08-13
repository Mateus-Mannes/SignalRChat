using Aspire.Hosting;
using Aspire.Hosting.Testing;
using System.Net;

namespace SignalRChat.EndToEndTests;

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
        await application.ResourceNotifications.WaitForResourceHealthyAsync(
            "nginx",
            cancellationSource.Token);

        using var client = application.CreateHttpClient("nginx", "http");
        BaseAddress = client.BaseAddress
            ?? throw new InvalidOperationException("The NGINX endpoint was not allocated.");
    }

    public async Task<(string FirstAffinity, string FirstInstance, string SecondAffinity, string SecondInstance)>
        FindDistinctAffinitiesAsync(CancellationToken cancellationToken = default)
    {
        string? firstAffinity = null;
        string? firstInstance = null;

        for (var index = 0; index < 32; index++)
        {
            var affinity = $"browser-affinity-{index}";
            var cookies = new CookieContainer();
            cookies.Add(BaseAddress, new Cookie("signalr_affinity", affinity));
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies })
            {
                BaseAddress = BaseAddress
            };
            using var response = await client.GetAsync("/account/me", cancellationToken);
            var instance = response.Headers.TryGetValues("X-SignalRChat-Instance", out var values)
                ? values.Single()
                : throw new InvalidOperationException("The API instance header was not present.");

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

    public async Task DisposeAsync()
    {
        if (application is not null)
        {
            await application.DisposeAsync();
        }
    }
}
