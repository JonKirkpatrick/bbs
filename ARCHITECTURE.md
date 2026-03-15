# Build-a-Bot Stadium Architecture

This document describes the architecture of the current runtime, not the earlier split-dashboard or violation-limit experiments.

Today the system runs as a single Go process with two interfaces:

* A TCP bot protocol on port `8080`
* An embedded HTTP dashboard on port `3000`

All live state is stored in memory under `stadium.DefaultManager`.

## System Overview

```mermaid
flowchart LR
    Bot1[Bot Client]
    Bot2[Bot Client]
    Spectator[Spectator Client]
    Browser[Dashboard Browser]
    Server[cmd/bbs-server]
    Manager[stadium.DefaultManager]
    Arena[Arena Instances]
    Registry[games/registry.go]
    Game[GameInstance]

    Bot1 -->|TCP commands| Server
    Bot2 -->|TCP commands| Server
    Spectator -->|TCP WATCH| Server
    Browser -->|HTTP GET /| Server
    Browser -->|SSE /arenas-sse| Server
    Server --> Manager
    Manager --> Arena
    Arena --> Game
    Server --> Registry
    Registry --> Game
    Manager -->|arena snapshots| Browser
```

## Design Summary

The executable in `cmd/bbs-server` owns both the bot server and the dashboard server. That matters because the dashboard reads arena state directly from the same in-memory manager instance that the TCP command handlers modify.

There is no persistence layer, job queue, or external message bus. If the process restarts, sessions and arenas are lost.

The current live game registry exposes `connect4`. Other game packages may exist in the repository, but only registered games can be created at runtime.

## Major Components

### 1. TCP Command Surface

`cmd/bbs-server/main.go` accepts raw TCP connections on port `8080` and handles the bot command loop.

Responsibilities:

* create a `stadium.Session` per connection
* enforce registration before most commands
* translate text commands into manager operations
* serialize responses as newline-delimited JSON
* clean up session and arena references on disconnect

This layer is intentionally thin. It owns transport and command parsing, but not arena lifecycle policy.

### 2. Stadium Manager

`stadium.Manager` is the central in-memory coordinator.

Responsibilities:

* assign session IDs and arena IDs
* track active sessions
* create and mutate arenas
* attach players and observers
* summarize arena state for the dashboard
* publish arena-list snapshots to dashboard subscribers
* run a watchdog for stale arena cleanup

The manager protects its mutable maps with a single `sync.Mutex`. That keeps the model simple, at the cost of coarse-grained locking.

### 3. Arena Model

An `Arena` represents one match or lobby.

Important fields:

* `Player1`, `Player2`
* `Observers`
* `Status`
* `Game`
* `TimeLimit`
* `Bot1Time`, `Bot2Time`
* `LastMove`

In practice the current statuses used by the code are:

* `waiting`
* `active`
* `completed`
* `aborted`

### 4. Game Plug-in Boundary

Games implement `games.GameInstance`:

* `GetName()`
* `GetState()`
* `ValidateMove()`
* `ApplyMove()`
* `IsGameOver()`

The server resolves a requested game through `games.GetGame()`, which uses `games/registry.go` as the registration table.

This is the main extension point for adding new games.

### 5. Embedded Dashboard

`cmd/bbs-server/dashboard.go` starts an HTTP server on port `3000` in the same process as the TCP server.

Responsibilities:

* serve the dashboard HTML at `/`
* open an SSE stream at `/arenas-sse`
* subscribe to `stadium.DefaultManager`
* render each arena snapshot through the HTML template

The dashboard does not poll and does not maintain its own copy of state.

## Runtime Flows

### Bot Registration And Arena Creation

```mermaid
sequenceDiagram
    participant Bot as Bot Client
    participant TCP as TCP Handler
    participant M as Stadium Manager
    participant R as Game Registry
    participant A as Arena
    participant D as Dashboard SSE

    Bot->>TCP: REGISTER bot_one connect4
    TCP->>M: RegisterSession(session, name, caps)
    M-->>TCP: session ID assigned
    TCP-->>Bot: {status:"ok", type:"register", ...}

    Bot->>TCP: CREATE connect4 1000 false
    TCP->>R: GetGame("connect4", args)
    R-->>TCP: GameInstance
    TCP->>M: CreateArena(game, 1000ms, false)
    M->>A: create status=waiting
    M-->>D: arena_list snapshot
    TCP-->>Bot: {status:"ok", type:"create", payload:arenaID}
```

### Join, Play, And Spectate

```mermaid
sequenceDiagram
    participant P1 as Player 1
    participant P2 as Player 2
    participant W as Watcher
    participant TCP as TCP Handler
    participant M as Stadium Manager
    participant A as Arena
    participant G as GameInstance
    participant D as Dashboard SSE

    P2->>TCP: JOIN <arena> <name> <handicap>
    TCP->>M: JoinArena(arenaID, session, handicap)
    M->>A: attach player
    M->>A: activateArena()
    A-->>P1: info/game start
    A-->>P2: info/game start
    M-->>D: arena_list snapshot

    W->>TCP: WATCH <arena>
    TCP->>M: AddObserver(arenaID, watcher)
    TCP-->>W: current game state

    P1->>TCP: MOVE 3
    TCP->>A: timeout check using LastMove and TimeLimit
    alt within time limit
        TCP->>G: ApplyMove(playerID, move)
        G-->>TCP: updated state
        A-->>P1: move accepted
        A-->>P2: update event
        A-->>W: update event
        A-->>P1: data/state
        A-->>P2: data/state
        A-->>W: data/state
    else move timed out
        A-->>all participants: error
        A->>A: status = completed
        M-->>D: arena_list snapshot
        TCP-->>P1: timeout response
    end
```

### Dashboard Subscription Flow

```mermaid
sequenceDiagram
    participant Browser as Browser
    participant HTTP as Dashboard HTTP Handler
    participant M as Stadium Manager

    Browser->>HTTP: GET /
    HTTP-->>Browser: dashboard HTML

    Browser->>HTTP: GET /arenas-sse
    HTTP->>M: Subscribe()
    M-->>HTTP: subscriber channel
    HTTP-->>Browser: initial arena snapshot

    Note over M: arena create/join/leave/cleanup
    M->>M: broadcastArenaListLocked()
    M-->>HTTP: StadiumEvent{Type:"arena_list", Payload:[]ArenaSummary}
    HTTP-->>Browser: SSE event with rendered HTML

    Browser-->>HTTP: disconnect
    HTTP->>M: Unsubscribe(channel)
```

## State Ownership

The architecture is intentionally centralized.

* `Session` owns per-connection identity and the socket write lock.
* `Manager` owns session registration, arena maps, arena summaries, and subscriber lists.
* `Arena` owns match participation, observer membership, and a concrete `GameInstance`.
* `GameInstance` owns game-specific validation and board state.

Because the manager owns the authoritative arena maps, the dashboard is implemented as a subscriber to manager snapshots rather than as an independent reader or a separate process.

## Concurrency Model

There are three main sources of concurrency:

* one goroutine per TCP client connection
* one goroutine for the HTTP dashboard server, with one handler goroutine per SSE client
* one watchdog goroutine for periodic arena cleanup

Synchronization strategy:

* `Manager.mu` protects arena maps, session maps, ID counters, and subscriber registration
* `Session.mu` protects concurrent writes to a single network connection
* dashboard subscriptions use buffered channels (`chan StadiumEvent, 10`) so the manager does not block on a slow browser

The tradeoff is that subscriber delivery is best-effort. If a subscriber channel is full, the event is dropped instead of blocking the server.

## Watchdog And Cleanup

The watchdog runs every 10 seconds and expires arenas according to status:

* `waiting`: after 1 hour
* `active`: after 3x the configured move time limit
* `completed`: after 1 minute

On cleanup the manager notifies participants, deletes the arena, and publishes a new arena snapshot to the dashboard.

## Current Boundaries And Gaps

The current design is workable, but it has clear boundaries:

* all state is in memory only
* process restart drops every session and arena
* one manager mutex serializes all arena and session mutations
* dashboard events are full arena-list snapshots, not fine-grained diffs
* the transport layer still mixes plain text writes and JSON writes in some command paths
* `GameInstance.IsGameOver()` exists as an interface seam, but match completion is not yet fully orchestrated through a dedicated game-over flow in the command loop

These are reasonable areas for future cleanup, but they do not change the core architecture described above.

## Structural Model

```mermaid
classDiagram
    class Session {
        +int SessionID
        +net.Conn Conn
        +string BotName
        +int PlayerID
        +Arena CurrentArena
        +[]string Capabilities
        +bool IsRegistered
        +SendJSON(Response)
    }

    class Response {
        +string Status
        +string Type
        +interface Payload
    }

    class Manager {
        +map Arenas
        +map ActiveSessions
        +map subscribers
        +int nextArenaID
        +int nextSessionID
        +RegisterSession(Session, name, caps)
        +CreateArena(GameInstance, timeLimit, allowHandicap)
        +JoinArena(id, Session, handicap)
        +AddObserver(id, Session)
        +HandlePlayerLeave(Session)
        +ListMatches()
        +Subscribe()
        +Unsubscribe(chan)
        +PublishArenaList()
    }

    class Arena {
        +int ID
        +Session Player1
        +Session Player2
        +[]Session Observers
        +bool AllowHandicap
        +string Status
        +GameInstance Game
        +Duration TimeLimit
        +Duration Bot1Time
        +Duration Bot2Time
        +Time LastMove
        +NotifyAll(type, payload)
        +NotifyOpponent(actorID, message)
    }

    class StadiumEvent {
        +string Type
        +interface Payload
    }

    class GameInstance {
        <<interface>>
        +GetName()
        +GetState()
        +ValidateMove(playerID, move)
        +ApplyMove(playerID, move)
        +IsGameOver()
    }

    Manager "1" *-- "*" Arena : manages
    Manager "1" *-- "*" Session : tracks
    Manager "1" o-- "*" StadiumEvent : publishes
    Arena "1" o-- "0..2" Session : players
    Arena "1" o-- "*" Session : observers
    Arena "1" --> "1" GameInstance : hosts
    Session --> Response : sends
```
