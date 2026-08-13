# SignalR Chat: Learning Roadmap from Demo to Scalable Architecture

## Summary

Evolve the existing SignalR broadcast demo incrementally into a reliable group-chat system. Each phase should remain runnable through Aspire with multiple API instances. The goal is to learn the architectural patterns locally—not deploy or benchmark a production system.

Complete one phase at a time, including its tests and failure cases, before moving forward.

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

## Phase 1 — Stabilize the shared database lifecycle

- Keep PostgreSQL as the shared relational database introduced in Phase 0.
- Move migration ownership from the first API replica to a dedicated migration resource if that exercise helps clarify startup responsibilities.
- Add explicit database reset and seed tooling for repeatable study scenarios.
- Add integration tests that prove both API replicas read and write the same database.

### Decisions and concerns

- Decide whether first-replica migration ownership is sufficient for the learning demo or whether to add a dedicated migration project now.
- Decide whether reset/seed is a CLI command, a Development-only endpoint, or a one-shot Aspire resource.
- Keep normal local data persistent while making every automated test run disposable and isolated.
- Decide when the 1 GB study settings should be replaced by settings based on observed workload and available memory.

### Completion criteria

- Multiple API replicas read and write the same database.
- Startup does not cause competing migrations.
- A developer can deliberately reset and seed the study data.

## Phase 2 — Add the chat domain

Introduce:

- `Conversation`: identity, name, creation time, and next message sequence.
- `ConversationMember`: conversation, user, join time, optional leave time, and optional role.
- `Message`: server ID, conversation, sender, `clientMessageId`, sequence, body, and creation time.
- `OutboxMessage`: event type, payload, state, attempts, lease information, timestamps, and last error.

Add APIs for:

- Creating and listing conversations.
- Adding, listing, and removing members.
- Enforcing the ten-member limit.
- Retrieving a conversation only when the caller is a member.

Database constraints:

- Unique membership per conversation and user.
- Unique sequence per conversation.
- Unique `(conversationId, senderId, clientMessageId)` for idempotency.

### Decisions and concerns

- Conversation naming and creator/administrator roles.
- Whether removed members retain access to old history.
- Whether users can leave conversations themselves.
- ID format and message-size limits.
- Whether edits, deletion, read receipts, and attachments remain out of scope.

### Completion criteria

- Authenticated users can create a conversation with up to ten members.
- Unauthorized users cannot inspect or modify it.

## Phase 3 — Build the durable send path

Change `SendMessage` to accept:

```text
conversationId
clientMessageId
body
```

In one database transaction:

1. Authenticate the sender.
2. Verify active membership.
3. Allocate the next conversation sequence safely.
4. Insert the message.
5. Insert its outbox event.
6. Commit.
7. Return the stored message as the sender acknowledgment.

Do not broadcast directly from the hub during this phase.

### Decisions and concerns

- Choose an atomic sequencing mechanism appropriate to the database; never use `MAX(sequence) + 1`.
- Define validation and authorization errors.
- Decide how many times sequence-allocation conflicts are retried.
- Define acknowledgement semantics: acknowledged means durably stored, not delivered.
- Handle the “commit succeeded but response was lost” case through `clientMessageId`.

### Completion criteria

- Concurrent senders receive unique, increasing sequences.
- Retrying the same `clientMessageId` returns the existing message.
- An acknowledged message always exists in the database with an outbox record.

## Phase 4 — Add history and conversation UI

Add cursor-based history endpoints:

```http
GET /conversations/{id}/messages?afterSequence=100&limit=100
GET /conversations/{id}/messages?beforeSequence=100&limit=50
```

- Use `afterSequence` for forward synchronization.
- Use `beforeSequence` for loading older history.
- Return messages ordered by sequence with pagination metadata.
- Add a basic conversation list, selected conversation, membership controls, and history view.
- Route `/conversations` through NGINX to the API pool.

### Decisions and concerns

- Maximum page sizes.
- Empty-history and deleted/left-conversation behavior.
- Whether pagination cursors remain plain sequence numbers or later become opaque.
- Whether the selected conversation is remembered locally.

### Completion criteria

- A user can switch conversations and load history.
- Pagination remains stable while new messages are being created.
- Nonmembers receive no message history.

## Phase 5 — Add authorized SignalR groups

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
- Users connected to different API replicas receive the same group events through Redis.

## Phase 6 — Implement the outbox delivery worker

- Add a hosted worker that polls pending outbox records.
- Atomically claim records using an owner and expiring lease.
- Broadcast the immutable message payload through `IHubContext<ChatHub>`.
- Mark the record processed after broadcasting.
- Retry transient failures with bounded exponential backoff.
- Move repeatedly failing records into a failed/dead-letter state.
- Recover records whose worker lease expired.

Run a worker in every API replica initially to demonstrate competing consumers. Extracting it into a separate Worker project can be a later exercise.

### Decisions and concerns

- Polling interval, batch size, lease duration, and retry limit.
- PostgreSQL claiming strategy, for example a short transaction using `FOR UPDATE SKIP LOCKED` and an expiring lease.
- Payload stored in the outbox versus reloading the message.
- Cleanup and retention of processed outbox records.
- Outbox delivery is at least once: a crash after broadcast but before completion can produce duplicates.

### Completion criteria

- Persisted messages are delivered without the request thread broadcasting them.
- Stopping delivery temporarily creates a backlog that drains after recovery.
- Multiple workers do not normally process the same lease simultaneously.
- A duplicate broadcast does not create a duplicate database message.

## Phase 7 — Make the browser resilient

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

## Phase 8 — Add failure-oriented tests

Use small deterministic tests and manual failure drills instead of load testing.

Test:

- Two users sending concurrently to one conversation.
- Duplicate `clientMessageId` submissions.
- API failure before commit.
- API failure after commit but before acknowledgment.
- Worker failure before broadcast.
- Worker failure after broadcast but before outbox completion.
- Worker lease expiration.
- One API replica being stopped during an active connection.
- Redis outage and recovery.
- Reconnection through another API replica.
- Messages created while a client is offline.
- WebSocket, SSE, and Long Polling behavior.
- Unauthorized group joins and history queries.

### Decisions and concerns

- Which failures are automated integration tests and which remain documented manual Aspire exercises.
- How to expose controlled failure injection safely in Development only.
- Whether each test needs the complete PostgreSQL/Redis/NGINX AppHost or can use a narrower integration fixture.

### Completion criteria

- No acknowledged message is lost.
- A client retry does not create a second stored message.
- Sequences remain unique.
- Clients recover gaps after reconnection.
- Outbox failures are visible and recoverable.

## Phase 9 — Add observability

- Add structured logging with conversation, message, client message, sequence, outbox, user, and API-instance identifiers.
- Trace durable-send, outbox-delivery, SignalR-broadcast, and synchronization operations.
- Add local metrics for send latency, active connections, reconnects, outbox backlog, retries, failures, and synchronization gaps.
- Separate liveness from readiness checks.
- Display traces, logs, and metrics in the Aspire dashboard.

### Decisions and concerns

- Avoid logging message bodies, authentication data, or other sensitive content.
- Define useful warning thresholds for the local demo.
- Decide whether a failed outbox item should make readiness unhealthy or only emit an alert.

### Completion criteria

- A message can be followed from client command through storage, outbox, worker, and recipient.
- Forced failures have an obvious explanation in the Aspire dashboard.

## Phase 10 — Study the production-scale evolution

Do not implement this phase unless it supports a specific learning goal. Document how the local components would evolve:

| Local learning component | Large-scale counterpart |
|---|---|
| NGINX | Global and regional ingress, such as Azure Front Door |
| API-hosted SignalR connections | Azure SignalR Service or sharded connection gateways |
| Redis backplane | Managed SignalR connection and fan-out tier |
| PostgreSQL demo database | Partitioned message store |
| API-hosted outbox worker | Independently scaled delivery workers |
| Database outbox polling | Change feed or durable message broker |
| Single region | Regional deployment stamps |
| Sequence per conversation | Conversation-partitioned ordering model |

### Study concerns

- Partitioning by conversation and handling hot conversations.
- Multi-region ownership and ordering.
- Connection sharding.
- Durable brokers versus database change feeds.
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

- This is a study project; production deployment, real capacity testing, and cloud infrastructure are excluded.
- Small concurrency tests and deliberate process failures remain included because they demonstrate correctness.
- Redis, NGINX, multiple API instances, normal SignalR negotiation, and sticky sessions remain part of the local topology.
- PostgreSQL is the relational baseline for studying concurrent sequencing, transactions, constraints, and outbox leasing.
- Delivery is at least once; deduplication and history recovery provide effective user-visible reliability.
- Exactly-once distributed delivery, attachments, read receipts, typing indicators, push notifications, moderation, and multi-region deployment are optional later exercises.
