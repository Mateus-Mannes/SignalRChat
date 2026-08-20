# Current Architecture — Phase 1

Phase 1 adds the relational conversation domain while retaining the Phase 0 SignalR behavior. PostgreSQL now owns users, conversations, memberships, and roles. Chat messages are still global and non-persistent so durable Cosmos DB messages can be introduced independently in Phase 2.

## Request and message flow

```text
Browser A ─┐
           ├── HTTP :8080 ──> NGINX ──┬──> SignalRChat.Web
Browser B ─┘                           │
                                      ├──> SignalRChat.Api 1 ─┐
                                      └──> SignalRChat.Api 2 ─┤──> PostgreSQL
                                                              │      ├── Identity users
                                      Redis <─────────────────┘      ├── Conversations
                                        │                            └── Memberships
                                        └── SignalR backplane fan-out
```

Aspire starts and observes every resource. NGINX is the single browser-facing endpoint. `/`, static assets, and Razor Pages go to the web process. `/register`, `/login`, `/logout`, `/account/*`, `/conversations`, and `/chatHub` go to the API pool.

## Conversation domain

`Conversation` has a GUID, a trimmed name, an immutable creator, and a UTC creation time. `ConversationMember` uses `(ConversationId, UserId)` as its key and stores an `Owner` or `Member` role, join time, and optional leave time.

Creating a conversation also creates its owner membership in the same EF Core save operation. The owner is permanent in Phase 1. Ordinary members can leave, and the owner can remove them. Leaving or removal sets `LeftAtUtc`; it does not delete the row. Re-adding that user reactivates the row with a new join time.

Only active members can retrieve a conversation or its member list. The API deliberately returns the same `404 conversation_not_found` response for a missing conversation and one hidden from the caller. The conversation list is newest-first and uses a bounded opaque cursor.

## Cross-replica membership consistency

One conversation can contain at most ten active members, including its owner. This is an application invariant because a normal relational check constraint cannot count other rows.

Each add, remove, leave, or reactivation operation starts a PostgreSQL transaction and obtains `SELECT ... FOR UPDATE` on the conversation row before inspecting membership. Requests for one conversation are therefore serialized even when they reach different API replicas. The active count is checked only after the lock, preventing two requests from both claiming the tenth slot.

Aspire's PostgreSQL integration configures EF Core connection resiliency. The complete row-lock transaction runs through EF Core's execution strategy so manually created transactions remain compatible with that retry behavior.

## SignalR affinity and transports

The browser creates a random `signalr_affinity` cookie. NGINX consistently hashes that value to select an API replica. This keeps the negotiation request and subsequent WebSocket, Server-Sent Events, or Long Polling requests on the same server. It is routing affinity, not durable session storage.

The client uses normal SignalR negotiation. `skipNegotiation` is not enabled, so WebSockets, Server-Sent Events, and Long Polling remain available. If an API process fails, the existing connection is lost; automatic reconnect and missed-message synchronization are intentionally deferred to Phase 6.

## Shared state

PostgreSQL stores ASP.NET Core Identity users plus conversation metadata and is shared by all API replicas. Because this is a local study application, the first API replica applies Entity Framework migrations before later replicas become healthy.

Redis has two separate responsibilities:

- The SignalR backplane forwards a broadcast made by one API replica to clients connected to every replica.
- The shared ASP.NET Core Data Protection key ring lets every replica decrypt the same authentication cookie.

NGINX and Redis do not preserve a live SignalR connection after its API process fails. Redis also does not persist chat history; its backplane traffic is transient.

## Resource lifecycle

Normal AppHost runs use named Docker volumes for PostgreSQL and Redis and expose NGINX at `http://localhost:8080`. `SignalRChat:ReplicaCount` controls how many separate API project resources are created. PostgreSQL uses `64 MB` shared buffers, at most `40` connections, and `1 MB` work memory per operation so the study topology can run inside the current 1 GB Docker allocation.

Automated tests launch the same AppHost model with two API replicas, disposable container data, and random host ports. This prevents a test run from changing normal development data or colliding with a manually running AppHost.

## Executable behavior

The xUnit integration suite verifies:

- NGINX and the web application are reachable.
- Registration, cookie login, authenticated account lookup, and logout work.
- Repeated requests with one affinity value remain on one API replica.
- An authentication cookie issued through one replica works through another replica.
- SignalR negotiation advertises WebSockets, Server-Sent Events, and Long Polling.
- A client can connect explicitly with each transport.
- Redis carries the current `Clients.All` broadcast between different API replicas.
- Conversation creation, normalized names, duplicate names, retrieval, and cursor pagination.
- Owner/member permissions, immediate access loss, leaving, removal, and reactivation.
- The ten-member limit under two concurrent requests routed to different API replicas.

The Playwright Chromium tests create independent browser contexts on different API replicas. They verify two-way cross-replica messaging and exercise conversation creation, adding a registered member, discovering the conversation from the other browser, and leaving it through the UI.

## Current phase boundary

- Every chat message still uses `Clients.All`; it is not associated with the new conversations.
- Chat messages are not stored and cannot be recovered after disconnecting.
- The browser does not reconnect or synchronize missed messages.
- Delivery acknowledgment, idempotency, per-conversation sequencing, and the outbox do not exist yet.
- Cosmos DB and Azure Service Bus are not part of the runtime yet.
- The browser has intentionally basic conversation and membership controls for studying the Phase 1 APIs. Selecting a conversation does not scope SignalR messages yet.
- API failover ends a live connection even though a later reconnect could select a healthy replica.

The `X-SignalRChat-Instance` response header is intentionally retained to make routing behavior visible in tests and browser developer tools.
