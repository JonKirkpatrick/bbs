# BBS Client (Track A)

This directory contains the Avalonia desktop client scaffold for Track A.

## Project Structure

- `src/Bbs.Client.App`: Avalonia desktop app shell and view models
- `src/Bbs.Client.Core`: shared contracts and core abstractions
- `src/Bbs.Client.Infrastructure`: local infrastructure implementations (logging, paths)
- `Bbs.Client.sln`: solution file

## Build

```bash
dotnet build bbs-client/Bbs.Client.sln
```

## Run

```bash
dotnet run --project bbs-client/src/Bbs.Client.App/Bbs.Client.App.csproj
```

## Logs

Structured local logs are written to:

- `$XDG_STATE_HOME/bbs-client/logs/client.log` when `XDG_STATE_HOME` is set
- `~/.local/state/bbs-client/logs/client.log` otherwise

## Storage

SQLite storage is initialized automatically on startup at:

- `$XDG_STATE_HOME/bbs-client/data/client.db` when `XDG_STATE_HOME` is set
- `~/.local/state/bbs-client/data/client.db` otherwise

## Current UI Shell

The app currently launches into a unified workspace shell with:

- Left panel: bot cards
- Center panel: activity workspace host
- Right panel: server cards

Bot registration/edit is available in the center workspace, and saved bot profiles persist in SQLite and reload into the left panel on startup.
