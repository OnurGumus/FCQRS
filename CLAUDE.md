# FCQRS Project Context for Claude

## Project Overview
FCQRS is an F# framework implementing Command Query Responsibility Segregation (CQRS) with Event Sourcing using Akka.NET actors. 

**Philosophy**: Use enterprise-grade distributed patterns for all applications (including CRUD) because the reliability and consistency guarantees are valuable from day one, and the framework handles the complexity overhead automatically.

## Architecture Components

### Core Modules
- **FCQRS** - Core framework with actor implementation
- **FCQRS.Model** - Domain modeling with validation and optics  
- **FCQRS.Serialization** - Custom serialization support
- **FCQRS.SQLProvider** - Database query provider
- **saga_sample/** - Saga pattern examples
- **sample/** - Basic CQRS examples

### Key Patterns

**Complete CQRS Flow:**
1. **Command Side**: Commands → Aggregates (cluster-sharded actors) → Events persisted
2. **Event Distribution**: Events flow to both read side and saga orchestration
3. **Read Side**: Events automatically update projections/read models 
4. **Query Side**: Optimized queries against read models
5. **Client Coordination**: CID-based subscriptions ensure clients know when read side is updated
6. **Side Effects**: Sagas handle external operations (emails, APIs) with retry/compensation

**Core Types:**
```fsharp
// Commands carry business intent
type Command<'CommandDetails> = {
    CommandDetails: 'CommandDetails
    CreationDate: DateTime
    Id: MessageId option
    Sender: ActorId option
    CorrelationId: CID
}

// Events represent what happened
type Event<'EventDetails> = {
    EventDetails: 'EventDetails
    CreationDate: DateTime
    Id: MessageId option
    Sender: ActorId option
    CorrelationId: CID
    Version: Version
}
```

### Domain Modeling
- **Validated types**: ShortString, LongString, CID, ActorId with ValueLens patterns
- **Predicate system**: For complex queries (Greater, Equal, And, Or, etc.)
- **Aether optics**: Functional lenses for nested data access
- **Validation framework**: Type-safe validation with detailed error reporting

### Actor System
- **Aggregates**: Business entities as cluster-sharded actors
- **Sagas**: Long-running processes, named as `originatorId__~Saga~_CID`
- **Event sourcing**: State rebuilt from events, snapshots every 30 events
- **Akka.NET integration**: Full clustering and distribution capabilities

## Key Files

### Model Layer (`/src/FCQRS.Model/`)
- `Model.fs` - Core domain types, validation, ValueLens patterns
- `Query.fs` - Query interface and DataEvent types

### Core Framework (`/src/FCQRS/`)
- `Actor.fs` - Aggregate actor implementations
- `Common.fs` - Shared utilities and types
- `Saga.fs` - Saga/process manager implementations

### Examples
- `sample/Command.fs` - Basic command handling example
- `saga_sample/` - Saga orchestration patterns

## Development Commands
```bash
# Build project
dotnet build

# Run samples  
dotnet run --project sample
dotnet run --project saga_sample
```

## Technical Stack
- .NET 9
- F# with functional programming patterns
- Akka.NET for actor system
- Custom JSON serialization
- Optional SQL providers for queries

## Architecture Principles
- **Event Sourcing**: Complete audit trail, state reconstruction
- **CQRS**: Separate read/write models
- **Actor Model**: Thread-safe, distributed processing
- **Functional**: Immutable data, validation, type safety
- **Domain-Driven**: Rich domain modeling with validation

## Critical Implementation Details

### Aggregate Development Pattern
1. **Create isolated functions**: `handleCommand` and `applyEvent` functions
2. **Wire with Akka.NET**: Use `init` and `initFactory` for actor system integration
3. **Cluster sharding**: Actors distributed across cluster nodes as virtual actors
4. **Garbage collection**: Actors can be passivated when inactive
5. **Thread safety**: Each actor processes messages sequentially

### Command/Event Flow with Saga Integration
1. **Command processing**: Sender subscribes to commands, waits for condition to yield true
2. **Event validation**: Events checked if they start a saga
3. **Saga startup sequence**:
   - Thread locked via `Ask` pattern
   - Event sent to saga starter
   - Saga persists initial event as `SagaStartingEvent`
   - Saga subscribes to mediator
   - Continue message sent to originator actor
   - Originator can continue processing
   - Event persisted and published
4. **Saga state management**: If event started saga, saga handles it again to switch states via `StateChangeEvent`

### Saga Architecture
- **Purpose**: Sagas take events and issue commands; Aggregates get commands and issue events
- **Naming convention**: `originator~~Saga~~correlationId`
- **Self-discovery**: Saga can find its originator from its name
- **Lifecycle**: Auto-remembered entities that auto-start
- **Correlation ID**: Critical for linking related messages and processes

### Key Concepts
- **Cluster-sharded actors**: Virtual actors that can be created anywhere in cluster
- **Passivation**: Automatic garbage collection of inactive actors
- **Thread safety**: Guaranteed through actor model's message-passing
- **Saga orchestration**: Long-running processes that coordinate between aggregates
- **Correlation tracking**: CID links commands, events, and sagas together

## Why Use FCQRS (Even for CRUD)

### Actors Perfect for Domain Aggregates
- **Natural fit**: Each domain entity = one actor with guaranteed consistency
- **Thread safety**: No locking, no race conditions, sequential message processing
- **Encapsulation**: Domain logic isolated with clear boundaries
- **Scalability**: Virtual actors distributed across cluster, passivated when idle

### Sagas Make Side Effects Reliable
- **Retryable operations**: Email sends, API calls, file operations can be retried
- **Compensation**: Failed operations can be rolled back properly
- **Consistency**: Side effects become part of event stream, not hidden external calls
- **Auditability**: All external interactions tracked and recoverable

### CQRS Benefits for All Applications
- **Performance**: Read models optimized independently from write operations
- **Real-time updates**: Events flow to read side, UI updates reactively
- **Multiple views**: Same events create different projections (tables, search, caches)
- **Evolution**: Add new read models without touching write side

### Client Coordination with CID
- **Predictable workflow**: Client subscribes to CID before sending command
- **Eventual consistency handled**: Client knows exactly when read side is updated
- **No polling**: Clean async pattern instead of "save then refresh" loops
- **Reliable UX**: Users get feedback when data is actually available

### Practical Example Flow
```
Traditional CRUD: POST /users → 201 → GET /users (potential stale data)

FCQRS Flow: 
1. Client subscribes to CID
2. POST /users with CID
3. Command processed by User aggregate
4. UserCreated event persisted and published
5. Event flows to read side, updates projection
6. Client notified via CID subscription
7. UI updates with fresh data
```

## Core Implementation Details

### Common.fs - Message Types & Infrastructure

**Core Message Types:**
- `Command<'CommandDetails>` - Commands with CID, timestamp, optional sender/message ID
- `Event<'EventDetails>` - Events with version, CID, timestamp, generated from commands
- Both implement `ISerializable` and `IMessageWithCID`

**EventActions:** Define what happens after command/event processing:
- `PersistEvent` - Save to journal, update state after persistence
- `DeferEvent` - Stash for later processing  
- `PublishEvent` - Immediate publish without persistence
- `IgnoreEvent/UnhandledEvent` - Control flow
- `StateChangedEvent` - Internal saga state transitions
- `Stash/Unstash/UnstashAll` - Message stashing support

**Saga Orchestration Types:**
- `ExecuteCommand` - Commands issued by sagas with target actors and optional delays
- `Effect` - Side effects (ResumeFirstEvent, StopActor, NoEffect)
- `SagaState<'SagaData,'State>` - Saga state with custom data and state machine
- `TargetActor` - Various ways to specify command targets (factory, actor ref, sender, self)

**IActor Interface:** Central API providing mediator, materializer, system access, and initialization methods for actors/sagas.

### Actor.fs - Aggregate Implementation

**Aggregate Actor Pattern:**
- **Persistent actors** with event sourcing (commands → events → state updates)
- **Cluster-sharded** virtual actors with automatic passivation
- **Snapshot support** every 30 events (configurable)
- **Version tracking** for optimistic concurrency control

**Key Flow:**
1. Commands processed by `handleCommand` function → `EventAction`
2. Events persisted via `PersistEvent` → journal storage
3. Events applied via `apply` function → state updates  
4. Events published to mediator for read side and sagas
5. Snapshots saved periodically for recovery optimization

**Command Subscription System:**
- Dynamic subscription actors for command correlation
- CID-based topic subscriptions for client coordination
- Temporary actors created per command subscription

### Saga.fs - Process Manager Implementation  

**Saga Actor Pattern:**
- **Long-running processes** that coordinate between aggregates
- **Event-driven** state machines with `SagaState<'SagaData,'State>`
- **Auto-remembered entities** that auto-start and never passivate
- **Named convention:** `originator~~Saga~~correlationId`

**Saga Lifecycle:**
1. **Startup:** Triggered by `SagaStartingEvent`, persisted for recovery
2. **Subscription:** Auto-subscribes to mediator for relevant events  
3. **State transitions:** Events processed by `handleEvent` → `StateChangedEvent`
4. **Side effects:** `applySideEffects` function determines commands to issue
5. **Command execution:** Issues commands to other actors with optional delays
6. **Completion:** Can trigger `StopActor` effect to terminate

**Advanced Features:**
- **Delayed commands** with scheduler integration
- **Target resolution** (factory, originator, sender, self)
- **Snapshot support** for long-running sagas
- **Recovery handling** with starting event replay

**Integration Points:**
- **Saga Starter** coordinates saga creation and synchronization
- **Mediator** handles event publishing and subscription
- **Correlation IDs** link commands, events, and sagas together
- **Command subscription** enables client coordination and eventual consistency handling

## Use Cases
**Excellent for**: Any application where data consistency, audit trails, and reliable side effects matter
**Especially valuable**: Business applications, financial systems, multi-user environments
**Consider alternatives**: Throwaway prototypes, purely functional systems without state