# Current Architecture — Phase 0

Phase 0 is an executable baseline for learning. It deliberately keeps global, non-persistent chat messages so later phases can introduce conversations, durable messages, the outbox, and reconnect synchronization one concept at a time.

## Request and message flow

```text
Browser A ─┐
           ├── HTTP :8080 ──> NGINX ──┬──> SignalRChat.Web
Browser B ─┘                           │
                                      ├──> SignalRChat.Api 1 ─┐
                                      └──> SignalRChat.Api 2 ─┤──> PostgreSQL
                                                              │
                                      Redis <─────────────────┘
                                        │
                                        └── SignalR backplane fan-out
```

Aspire starts and observes every resource. NGINX is the single browser-facing endpoint. `/`, static assets, and Razor Pages go to the web process. `/register`, `/login`, `/logout`, `/account/*`, and `/chatHub` go to the API pool.

## SignalR affinity and transports

The browser creates a random `signalr_affinity` cookie. NGINX consistently hashes that value to select an API replica. This keeps the negotiation request and subsequent WebSocket, Server-Sent Events, or Long Polling requests on the same server. It is routing affinity, not durable session storage.

The client uses normal SignalR negotiation. `skipNegotiation` is not enabled, so WebSockets, Server-Sent Events, and Long Polling remain available. If an API process fails, the existing connection is lost; automatic reconnect and missed-message synchronization are intentionally deferred to Phase 7.

## Shared state

PostgreSQL stores ASP.NET Core Identity users and is shared by all API replicas. The first API replica applies Entity Framework migrations before later replicas become healthy. Phase 1 revisits whether migration ownership should become a dedicated Aspire resource.

Redis has two separate responsibilities:

- The SignalR backplane forwards a broadcast made by one API replica to clients connected to every replica.
- The shared ASP.NET Core Data Protection key ring lets every replica decrypt the same authentication cookie.

NGINX and Redis do not preserve a live SignalR connection after its API process fails. Redis also does not persist chat history; its backplane traffic is transient.

## Resource lifecycle

Normal AppHost runs use named Docker volumes for PostgreSQL and Redis and expose NGINX at `http://localhost:8080`. `SignalRChat:ReplicaCount` controls how many separate API project resources are created. PostgreSQL uses `64 MB` shared buffers, at most `40` connections, and `1 MB` work memory per operation so the study topology can run inside the current 1 GB Docker allocation.

Automated tests launch the same AppHost model with two API replicas, disposable container data, and random host ports. This prevents a test run from changing normal development data or colliding with a manually running AppHost.

## Executable baseline

The xUnit integration suite verifies:

- NGINX and the web application are reachable.
- Registration, cookie login, authenticated account lookup, and logout work.
- Repeated requests with one affinity value remain on one API replica.
- An authentication cookie issued through one replica works through another replica.
- SignalR negotiation advertises WebSockets, Server-Sent Events, and Long Polling.
- A client can connect explicitly with each transport.
- Redis carries the current `Clients.All` broadcast between different API replicas.

The Playwright Chromium test creates two independent browser contexts, assigns them to different API replicas, registers two users, and verifies two-way cross-replica messaging and logout through the actual UI.

## Known limitations frozen for Phase 0

- Every message uses `Clients.All`; there are no conversations or membership checks.
- Chat messages are not stored and cannot be recovered after disconnecting.
- The browser does not reconnect or synchronize missed messages.
- Delivery acknowledgment, idempotency, per-conversation sequencing, and the outbox do not exist yet.
- API failover ends a live connection even though a later reconnect could select a healthy replica.

The `X-SignalRChat-Instance` response header is intentionally retained to make routing behavior visible in tests and browser developer tools.
