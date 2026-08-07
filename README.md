# SignalRChat

The Aspire AppHost runs the Razor Pages frontend, a configurable pool of SignalR API processes, Redis, and an NGINX ingress on `http://localhost:8080`.

## Prerequisites

- .NET 10 SDK
- Docker Desktop or another Aspire-compatible container runtime

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
- Only the first API process applies Entity Framework migrations. Remaining processes start after that instance becomes healthy.

API responses include an `X-SignalRChat-Instance` header to make local affinity testing visible. Open the site in separate browser profiles or clear the `signalr_affinity` cookie to obtain a different affinity key.
