```mermaid
sequenceDiagram
    participant B1 as Player Bot 1
    participant B2 as Player Bot 2
    participant S as Spectator Bot
    participant M as Stadium Manager
    participant G as Game Engine

    B1->>M: JOIN (Bot_A)
    Note over M: Bot_A added to Waiting Pool
    B2->>M: JOIN (Bot_B)
    M->>G: NewGameInstance()
    M->>B1: BEGIN (You are P1)
    M->>B2: BEGIN (You are P2)
    
    S->>M: LIST
    M-->>S: Active Matches: [0] Bot_A vs Bot_B
    S->>M: WATCH 0
    
    B1->>M: MOVE 3
    M->>G: ApplyMove(1, 3)
    G-->>M: OK (State Updated)
    
    par Notifications
        M->>B2: UPDATE: Opponent moved in Col 3
        M->>S: UPDATE: P1 moved in Col 3
    end
```

```mermaid
classDiagram
    class Session {
        +string BotName
        +net.Conn Conn
        +int PlayerID
        +Match CurrentMatch
    }

    class Match {
        +Session Player1
        +Session Player2
        +List~Session~ Observers
        +GameInstance Game
        +NotifyAll(msg)
    }

    class Manager {
        +List~Session~ WaitingPool
        +List~Match~ ActiveMatches
        +AddToWaitingRoom(Session)
        +ListGames() string
    }

    Manager "1" *-- "*" Match : orchestrates
    Match "1" o-- "2" Session : players
    Match "1" o-- "*" Session : observers
    Match "1" --> "1" GameInstance : hosts