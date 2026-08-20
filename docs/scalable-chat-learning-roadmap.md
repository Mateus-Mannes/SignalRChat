# SignalR Chat: Learning Roadmap from Demo to Scalable Architecture

## Summary

Evolve the existing SignalR broadcast demo incrementally into a reliable group-chat system. Each phase should remain understandable and runnable through Aspire with multiple API instances. The goal is to study the reliability model of a much larger architecture without deploying or load-testing a production system.

Complete one phase at a time, including its tests and failure cases, before moving forward.

## Target storage and delivery model

- PostgreSQL is authoritative for ASP.NET Core Identity users, conversations, memberships, and roles.
- Azure Cosmos DB for NoSQL is authoritative for message streams, per-conversation sequence state, idempotency records, and transactional outbox events.
- A Cosmos DB change-feed relay publishes committed outbox events to an Azure Service Bus session-enabled, partitioned queue.
- Service Bus delivery workers broadcast events through SignalR; Redis remains the local SignalR backplane and shared Data Protection key store.
- The API never performs a direct Cosmos DB plus Service Bus dual write. A message and its outbox event commit together in Cosmos DB before the sender is acknowledged.

## Phase 0 — Establish the baseline

**Status:** Implemented with native ARM64 PostgreSQL and a 1 GB local Docker constraint.

- Document the current request flow in `docs/current-architecture.md`.
- Replace SQLite with one PostgreSQL database shared by every API replica and hosted by Aspire.
- Add xUnit integration tests that start the real Aspire topology with disposable data and random host ports.
- Add a Playwright Chromium test using two isolated authenticated browser contexts.
- Preserve configurable API replicas, normal SignalR negotiation, all three transports, NGINX affinity, Redis fan-out, shared authentication cookies, global broadcast, and the diagnostic instance header.

### Decisions and concerns

- The baseline test frameworks are xUnit, Aspire.Hosting.Testing, and Playwright for .NET with Chromium.
- Topology tests launch the real AppHost rather than replacing Redis, PostgreSQL, or NGINX with test doubles.
- Normal development uses persistent Redis and PostgreSQL volumes; automated tests disable volumes and use random host ports.
- PostgreSQL runs natively on Apple Silicon and uses deliberately conservative settings for the 1 GB Docker constraint. Revisit those settings when studying realistic capacity rather than correctness.
- Record existing functional limitations: global broadcast, no chat-message persistence, no reconnect synchronization, and no conversation authorization.

### Completion criteria

- The solution builds and starts consistently.
- Two users can exchange the existing non-persistent messages through different API replicas.
- Registration, login, logout, cookie sharing, affinity, negotiation, WebSockets, SSE, Long Polling, and Redis cross-replica fan-out are covered by executable tests.

## Phase 1 — Add the chat domain

**Status:** Implemented with PostgreSQL-backed conversations and transactional membership management across API replicas.

Add relational conversation metadata to PostgreSQL:

- `Conversation`: identity, name, creator, and creation time.
- `ConversationMember`: conversation, user, join time, optional leave time, and optional role.

Add APIs for:

- Creating and listing conversations.
- Adding, listing, and removing members.
- Enforcing the ten-member limit.
- Retrieving a conversation only when the caller is a member.

PostgreSQL constraints:

- Unique membership per conversation and user.
- Foreign keys from conversations and memberships to `AspNetUsers`.
- A maximum of ten active members enforced transactionally by the application.

### Decisions and concerns

- Conversation IDs are GUIDs; names are trimmed, limited to 100 characters, and need not be unique.
- The creator is the immutable owner. Owners add or remove registered users by normalized email; ordinary members may leave.
- Removed members immediately lose all access, remain as inactive relational rows, and may be reactivated later.
- Conversation lists use bounded cursor pagination; member lists need no pagination because the active limit is ten.
- PostgreSQL row locking serializes membership changes for one conversation across API replicas.
- Keep messages and sequence counters out of PostgreSQL; they belong to the Cosmos DB message stream introduced in Phase 2.
- Edits, conversation deletion, ownership transfer, read receipts, and attachments remain out of scope.

### Completion criteria

- Authenticated users can create a conversation with up to ten members.
- Unauthorized users cannot inspect or modify it.
- Concurrent requests through different replicas cannot exceed the active-member limit.

## Phase 2 — Build the durable send path

Add an Aspire-managed Azure Cosmos DB for NoSQL emulator for local study. Create:

- `message-streams`, partitioned by `/conversationId`.
- `message-stream-leases`, used by the future change-feed processor.

Use document-type discriminators in `message-streams`:

- `stream`: the next sequence and concurrency ETag for one conversation.
- `message`: the immutable durable chat message.
- `idempotency`: a deterministic record for `(conversationId, senderId, clientMessageId)`.
- `outbox`: the immutable `chat.message-created.v1` delivery event.

Change `SendMessage` to accept:

```text
conversationId
clientMessageId
body
```

The agreed identifiers are GUIDs for conversation, server message, client message, and outbox event IDs.

For each send:

1. Authenticate the sender.
2. Verify active membership against PostgreSQL.
3. Check the deterministic Cosmos DB idempotency item.
4. Read the conversation stream state and ETag.
5. In one Cosmos DB transactional batch and one logical partition, conditionally advance the sequence and create the message, idempotency, and outbox documents.
6. Retry bounded ETag conflicts caused by concurrent senders.
7. Return the stored message as the sender acknowledgment.

Do not broadcast directly from the hub during this phase.

### Decisions and concerns

- Use Cosmos DB optimistic concurrency and a transactional batch; never use a query equivalent to `MAX(sequence) + 1`.
- All documents participating in the batch must share the same `conversationId` partition key.
- Define validation and authorization errors.
- Decide how many times sequence-allocation conflicts are retried.
- Acknowledged means the message, sequence update, idempotency record, and outbox event are committed to Cosmos DB—not delivered through Service Bus or SignalR.
- Handle the “commit succeeded but response was lost” case through `clientMessageId`.
- Accept and document the narrow consistency boundary between the PostgreSQL membership read and the Cosmos DB write; membership is checked again for history and group access.
- Decide Cosmos DB indexing, retention, and local-emulator initialization without introducing time-bucket partitioning prematurely.
- Validate the ARM64 Cosmos DB vNext emulator within the 1 GB Docker budget. If the complete topology is too large, use a narrower Cosmos-specific Aspire fixture rather than hiding the resource constraint.

### Completion criteria

- Concurrent senders receive unique, increasing sequences.
- Retrying the same `clientMessageId` returns the existing message.
- Reusing the same `clientMessageId` with different content is rejected.
- An acknowledged message always exists in Cosmos DB with its idempotency and outbox documents.
- No Service Bus publish occurs on the request thread.

## Phase 3 — Add history and conversation UI

Add cursor-based history endpoints:

```http
GET /conversations/{id}/messages?afterSequence=100&limit=100
GET /conversations/{id}/messages?beforeSequence=100&limit=50
```

- Use `afterSequence` for forward synchronization.
- Use `beforeSequence` for loading older history.
- Authorize through PostgreSQL, then query the specified Cosmos DB conversation partition.
- Return only `message` documents, ordered by sequence, with pagination metadata.
- Add a basic conversation list, selected conversation, membership controls, and history view.
- Build the UI against the Phase 1 `/conversations` route already exposed through NGINX.

### Decisions and concerns

- Maximum page sizes.
- Empty-history and deleted/left-conversation behavior.
- Whether pagination cursors remain plain sequence numbers or later become opaque.
- Whether the selected conversation is remembered locally.
- Cosmos DB continuation tokens versus the public sequence-based contract; do not expose provider tokens unless they are treated as opaque.

### Completion criteria

- A user can switch conversations and load history.
- Pagination remains stable while new messages are being created.
- Nonmembers receive no message history.

## Phase 4 — Add authorized SignalR groups

- Use one SignalR group per conversation.
- Add an authorized `JoinConversation` operation.
- Verify database membership before calling `Groups.AddToGroupAsync`.
- Add `LeaveConversation`.
- Remove access promptly when membership is revoked.
- Broadcast structured `MessageDto` events rather than `(user, text)` arguments.

### Decisions and concerns

- Join every user conversation or only the currently selected conversation.
- Group-name format and preventing user-controlled arbitrary group names.
- How membership removal affects existing live connections.
- Whether group restoration belongs in `OnConnectedAsync` or an explicit client synchronization command.

### Completion criteria

- Messages are visible only to members of the relevant conversation.
- Authorized users connected to different API replicas can join the same logical group through Redis.
- Durable message delivery is intentionally not connected to these groups until Phase 5.

## Phase 5 — Add Service Bus and asynchronous delivery

- Add Bicep under `infra/` that provisions a development Azure Service Bus namespace and a `chat-message-delivery` queue.
- Configure the queue with sessions, partitioning, duplicate detection, retry/dead-letter behavior, and conservative development capacity.
- Keep credentials out of the repository. Prefer Azure identity for local development and future managed identity; document any connection-string fallback.
- Add a Cosmos DB change-feed relay that filters immutable `outbox` documents and publishes `MessageCreatedV1` events to Service Bus.
- Set Service Bus `MessageId` to the outbox event ID and `SessionId` to the conversation ID. If `PartitionKey` is set explicitly, it must equal `SessionId`.
- Add session-aware delivery workers that consume each conversation in order and broadcast the payload through `IHubContext<ChatHub>`.
- Complete a Service Bus message only after the SignalR broadcast call succeeds; rely on retry and dead-letter behavior for repeated failures.

Use separate relay and delivery Worker projects so API scaling, change-feed processing, and delivery consumption have explicit responsibilities. The workers can still be launched and observed through Aspire.

### Decisions and concerns

- The queue is a deployed Azure development resource because the official local emulator requires more than the 1 GB Docker budget and does not emulate partitioned entities.
- Decide how opt-in cloud integration tests locate and isolate the shared development queue.
- The Cosmos DB change feed and Service Bus delivery are both at least once; the relay, worker, and browser must tolerate duplicates.
- Avoid updating an outbox document in a way that creates a change-feed feedback loop. Use change-feed leases/checkpoints and immutable event IDs.
- Decide retry limits, lock durations, duplicate-detection window, dead-letter inspection, and event retention.
- Decide whether future notification or analytics consumers justify replacing the queue with a topic; keep a queue for the current delivery pipeline.

### Completion criteria

- The Bicep deployment reproducibly creates the development Service Bus namespace and queue.
- Persisted Cosmos DB messages are relayed and delivered without the request thread publishing to Service Bus or broadcasting through SignalR.
- Stopping the relay or delivery worker creates a backlog that drains after recovery.
- Events for one conversation are processed through the same Service Bus session in sequence order.
- Redelivery can create duplicate real-time events but never a duplicate durable Cosmos DB message.
- Poison events become observable in the Service Bus dead-letter queue.

## Phase 6 — Make the browser resilient

- Enable `withAutomaticReconnect()`.
- Generate one stable `clientMessageId` per user send and reuse it on retries.
- Track `lastAppliedSequence` for each loaded conversation.
- Deduplicate incoming events by server `messageId`.
- Detect sequence gaps.
- After reconnecting, rejoin authorized groups and request messages after the last applied sequence.
- Buffer live events while history synchronization is running, then merge everything by sequence.
- Show sending, stored, reconnecting, synchronized, and failed UI states.

### Decisions and concerns

- Store synchronization cursors only in memory or in browser storage.
- Retry policy and when the UI asks the user to retry manually.
- Optimistic rendering versus waiting for the durable acknowledgment.
- Behavior when membership changes while disconnected.
- Maximum in-memory deduplication window.

### Completion criteria

- Refreshing or reconnecting reconstructs the conversation from durable history.
- Messages sent during disconnection appear after synchronization.
- Duplicate real-time deliveries do not appear twice.
- Sequence gaps trigger recovery rather than silently losing messages.

## Phase 7 — Add failure-oriented tests

Use small deterministic tests and manual failure drills instead of load testing.

Test:

- Two users sending concurrently to one conversation.
- Duplicate `clientMessageId` submissions.
- API failure before the Cosmos DB transactional batch commits.
- API failure after the Cosmos DB batch commits but before acknowledgment.
- Cosmos DB ETag conflicts during sequence allocation.
- Change-feed relay failure before and after Service Bus publish.
- Service Bus redelivery and dead-letter behavior.
- Delivery-worker failure before broadcast and after broadcast but before message completion.
- Change-feed lease reassignment when a relay instance stops.
- One API replica being stopped during an active connection.
- Redis outage and recovery.
- Reconnection through another API replica.
- Messages created while a client is offline.
- WebSocket, SSE, and Long Polling behavior.
- Unauthorized group joins and history queries.

### Decisions and concerns

- Which failures are automated integration tests and which remain documented manual Aspire exercises.
- How to expose controlled failure injection safely in Development only.
- Which tests use the local Cosmos DB emulator and which opt in to the deployed Service Bus development namespace.
- Whether each test needs the complete PostgreSQL/Cosmos DB/Redis/NGINX AppHost or can use a narrower integration fixture.
- Prevent parallel test runs from consuming or completing another run's Service Bus messages.

### Completion criteria

- No acknowledged message is lost.
- A client retry does not create a second stored message.
- Sequences remain unique.
- Clients recover gaps after reconnection.
- Change-feed, Service Bus, and delivery failures are visible and recoverable.

## Phase 8 — Add observability

- Add structured logging with conversation, message, client message, sequence, outbox, user, and API-instance identifiers.
- Trace PostgreSQL authorization, Cosmos DB durable-send, change-feed relay, Service Bus enqueue/dequeue, SignalR broadcast, and synchronization operations.
- Add metrics for send latency, Cosmos DB request charge and conflicts, change-feed lag, Service Bus queue/dead-letter depth, delivery retries, active connections, reconnects, and synchronization gaps.
- Separate liveness from readiness checks.
- Display traces, logs, and metrics in the Aspire dashboard.

### Decisions and concerns

- Avoid logging message bodies, authentication data, or other sensitive content.
- Define useful warning thresholds for the local demo.
- Decide whether a relay failure, Service Bus outage, or dead-letter backlog should affect readiness or only emit an alert.

### Completion criteria

- A message can be followed from client command through storage, outbox, worker, and recipient.
- Forced failures have an obvious explanation in the Aspire dashboard.

## Phase 9 — Study the production-scale evolution

Do not implement this phase unless it supports a specific learning goal. Document how the local components would evolve:

| Learning component | Large-scale evolution |
|---|---|
| NGINX | Global and regional ingress, such as Azure Front Door |
| API-hosted SignalR connections | Azure SignalR Service or sharded connection gateways |
| Redis backplane | Managed SignalR connection and fan-out tier |
| PostgreSQL identity and membership database | Scaled relational metadata store |
| Cosmos DB emulator message stream | Multi-region partitioned Cosmos DB message store |
| Cosmos DB change-feed relay | Independently scaled change-feed processors |
| Development Service Bus session queue | Capacity-planned regional messaging namespaces |
| Aspire-hosted delivery workers | Independently autoscaled delivery workers |
| Single region | Regional deployment stamps |
| Sequence per conversation | Conversation-partitioned ordering model |

### Study concerns

- Partitioning by conversation and handling hot conversations.
- Retention or archival before a conversation logical partition approaches its storage limit.
- Multi-region ownership and ordering.
- Connection sharding.
- Service Bus namespace, entity, and session sharding.
- Presence, offline notifications, retention, attachments, moderation, and deletion.
- Capacity based on concurrent connections and recipient fan-out—not registered-user count alone.

The output of this phase should be architecture notes or ADRs, not production infrastructure.

## Public interface evolution

Expected final learning-demo contracts:

```text
CreateConversation
ListConversations
AddMember
RemoveMember
GetMessages(afterSequence | beforeSequence, limit)

JoinConversation
LeaveConversation
SendMessage(conversationId, clientMessageId, body) -> MessageDto

MessageReceived(MessageDto)
```

`MessageDto` should consistently contain:

```text
messageId
clientMessageId
conversationId
sequence
senderId/display name
body
createdAt
```

## Assumptions and boundaries

- This is a study project; production deployment and real capacity testing are excluded.
- The one planned cloud dependency is a development Azure Service Bus namespace and queue provisioned by Bicep because its local emulator cannot reproduce the required partitioning within the 1 GB Docker constraint.
- Small concurrency tests and deliberate process failures remain included because they demonstrate correctness.
- Redis, NGINX, multiple API instances, normal SignalR negotiation, and sticky sessions remain part of the local topology.
- PostgreSQL stores identity and relational conversation metadata; it does not store message history or the message outbox.
- Cosmos DB stores durable messages, sequence state, idempotency records, and immutable outbox events in conversation partitions.
- Service Bus provides the durable delivery backlog; it is not the message-history source of truth.
- Delivery is at least once; deduplication and history recovery provide effective user-visible reliability.
- Exactly-once distributed delivery, attachments, read receipts, typing indicators, push notifications, moderation, and multi-region deployment are optional later exercises.

## Microsoft architecture references

- [Azure Cosmos DB partitioning](https://learn.microsoft.com/en-us/azure/cosmos-db/partitioning-overview)
- [Azure Cosmos DB transactional batch](https://learn.microsoft.com/en-us/azure/cosmos-db/transactional-batch)
- [Transactional outbox with Azure Cosmos DB](https://learn.microsoft.com/en-us/azure/architecture/databases/guide/transactional-outbox-cosmos)
- [Azure Cosmos DB Linux emulator vNext](https://learn.microsoft.com/en-us/azure/cosmos-db/emulator-linux)
- [Azure Service Bus partitioning and session keys](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-partitioning)
- [Azure Service Bus emulator limitations](https://learn.microsoft.com/en-us/azure/service-bus-messaging/overview-emulator)
