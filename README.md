# SignalRChat

The Aspire AppHost runs the Razor Pages frontend, a configurable pool of SignalR API processes, PostgreSQL, Redis, and an NGINX ingress on `http://localhost:8080`.

## Prerequisites

- .NET 10 SDK
- Docker Desktop or another Aspire-compatible container runtime
- Approximately 1 GB of memory allocated to the container runtime is sufficient for this study topology.

The official PostgreSQL image runs natively on Apple Silicon. PostgreSQL is configured with conservative local-development memory settings; these are not production sizing recommendations.

## Run the distributed application

```bash
dotnet run --project src/SignalRChat.AppHost
```

Open `http://localhost:8080`. The Aspire dashboard URL is printed by the AppHost.

The default replica count is configured in `src/SignalRChat.AppHost/appsettings.json`. Override it for a run without editing files:

```bash
SignalRChat__ReplicaCount=5 dotnet run --project src/SignalRChat.AppHost
```

The AppHost must be restarted when the replica count changes because the set of resources is created when the application model is built.

## How requests are routed

- NGINX proxies UI requests to `SignalRChat.Web`.
- Authentication, account, and `/chatHub` requests are sent to the API pool.
- The browser creates a non-sensitive `signalr_affinity` cookie. NGINX consistently hashes it so SignalR negotiation and the selected transport stay on the same API process.
- Redis forwards hub messages between API processes and stores the shared ASP.NET Core Data Protection key ring.
- PostgreSQL stores Identity data shared by all API processes.
- Only the first API process applies Entity Framework migrations. Remaining processes start after that instance becomes healthy.

API responses include an `X-SignalRChat-Instance` header to make local affinity testing visible. Open the site in separate browser profiles or clear the `signalr_affinity` cookie to obtain a different affinity key.

The API has a conventional `ConnectionStrings:signalrchat` entry in `appsettings.json`. EF Core design-time commands use it to construct the context without a design-time factory. During an Aspire run, `WithReference(database)` injects a connection string with the same name and overrides the local value. `Database:ApplyMigrations` defaults to `false`; the AppHost explicitly enables it only for the first API replica.

## Run the Phase 0 tests

The integration suite launches a disposable copy of the complete Aspire topology with two API replicas and random ports:

```bash
dotnet test tests/SignalRChat.IntegrationTests/SignalRChat.IntegrationTests.csproj
```

Install Playwright's Chromium browser once, then run the browser test:

```bash
dotnet build tests/SignalRChat.EndToEndTests/SignalRChat.EndToEndTests.csproj
pwsh tests/SignalRChat.EndToEndTests/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet test tests/SignalRChat.EndToEndTests/SignalRChat.EndToEndTests.csproj --no-build
```

Docker must be running. Run the two test projects separately on a memory-constrained laptop because each project owns its own PostgreSQL, Redis, NGINX, web, and API resources.

## Learning documents

- [Current Phase 0 architecture](docs/current-architecture.md)
- [Roadmap from demo to scalable architecture](docs/scalable-chat-learning-roadmap.md)

The implementation follows the Microsoft guidance for [hosting and scaling SignalR](https://learn.microsoft.com/en-us/aspnet/core/signalr/scale?view=aspnetcore-10.0), the [Redis backplane](https://learn.microsoft.com/en-us/aspnet/core/signalr/redis-backplane?view=aspnetcore-10.0), [Aspire integration testing](https://learn.microsoft.com/en-us/dotnet/aspire/testing/write-your-first-test), and [Aspire PostgreSQL hosting](https://learn.microsoft.com/en-us/dotnet/aspire/database/postgresql-integration).
