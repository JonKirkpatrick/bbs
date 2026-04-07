# MainWindowViewModel Comprehensive Inventory

> **Analysis Date**: April 7, 2026  
> **Scope**: Full file structure analysis across all partial files  
> **Partial Files**: 5 (main + 4 partials)  
> **Lines of Code**: ~2,200 (main) + partials

---

## 1. CURRENT SCOPE & MAJOR RESPONSIBILITIES

### Core Concerns Managed

| Concern Area | Responsibility | Status |
|---|---|---|
| **Bot Management** | Register, edit, save, deploy bots; manage bot profiles and runtime instances | Primary |
| **Server Management** | Register, probe, edit known servers; maintain server registry and cache | Primary |
| **Arena Operations** | View live arenas, create/join arenas via owner token actions, watch arena state | Primary |
| **Session Orchestration** | Deploy bot sessions to servers, manage active bot-session lifecycle, handle control socket handshakes | Primary |
| **UI State & Context** | Maintain workspace context, toggle panels, navigate between bot/server/arena views | Primary |
| **Server Discovery & Probing** | Periodic server health checks, plugin catalog caching, endpoint candidate resolution | Primary |
| **Owner Token Access** | Load, mask, and validate owner tokens; coordinate arena and bot control actions | Secondary |
| **Persona Management** | Load/unload/create/duplicate/rename/delete workspace personas (bot/server profiles) | Secondary |
| **Data Persistence** | Coordinate with storage layer for profiles, runtime state, and plugin caches | Support |
| **Orchestration Integration** | Launch bots, manage lifecycle state, handle deployment exceptions | Support |

---

## 2. FILE STRUCTURE: HOW MAINWINDOWVIEWMODEL IS SPLIT

### File Organization

```
MainWindowViewModel.cs (Main - ~2,200 lines)
├── Constructor, Dependencies, Constants
├── Properties (UI state, collections, editor fields)
├── Commands (public ICommand definitions)
├── Core Methods (context switching, deployment, token actions)
├── Session Management (active session tracking, reconciliation)
├── Helper Methods (profile builders, endpoint normalization)
└── Metadata Resolution Methods

MainWindowViewModel.Personas.cs (Partial - ~140 lines)
├── Persona Loading/Unloading
├── Person Creation/Duplication/Renaming/Deletion
├── PersonaManager Integration
└── Runtime Replacement for Personas

MainWindowViewModel.ServerDiscovery.cs (Partial - ~510 lines)
├── Server Probing Cycle (startup & manual)
├── Server Reachability Checks (HTTP /api/status)
├── Plugin Catalog Fetching & Caching
├── Owner Token Retrieval
├── Endpoint Candidate Building for Servers
├── Status Parsing & Validation
└── Refresh Coordination (cooldowns, in-flight tracking)

MainWindowViewModel.ArenaWatcher.cs (Partial - ~380 lines)
├── Arena Viewer Initialization
├── Live Arena Polling (900ms intervals)
├── Arena Viewer HTML/URL Projection
├── Embedded Viewer Support Configuration
├── Arena State Updates & Reconciliation
├── Watch Loop Management (Start/Stop)
├── Browser Launch Integration
└── Diagnostics Reporting

MainWindowViewModel.Types.cs (Partial - ~120 lines)
├── Record Types (ServerMetadataEntryItem, ServerPluginCatalogItem, etc.)
├── Response DTOs (RegisterHandshakeResult, AgentControlResponse)
├── ViewModel Bindable Classes (ActiveBotSessionItem)
├── Enums (WorkspaceContext)
└── Type Definitions for UI Data

MainWindowViewModelHelpers.cs (Helper Class - ~180 lines)
├── Token Masking/Unmasking
├── Metadata Parsing & Formatting
├── Endpoint Building
├── JSON Property Extraction
├── Status Validation Helpers
└── Port Parsing Utilities
```

### Why This Split?

- **Personas**: Lifecycle ortho­gonal to main VM (can be loaded/unloaded independently)
- **ServerDiscovery**: HTTP/network operations (clear boundary); probe cycle is stateful
- **ArenaWatcher**: Polling loop + embedded viewer logic (high cohesion but separate concern)
- **Types**: Data contract objects for UI binding (reusable, independent)
- **Helpers**: Utilities used across partials (promotes DRY)

---

## 3. DEPENDENCY PATTERNS

### External Dependencies (Injected)

```csharp
// Constructor Parameters
private readonly IClientLogger _logger;                          // Event/diagnostics logging
private readonly PersonaManager? _personaManager;                // Persona file lifecycle
private IClientStorage _storage;                                 // Bot/server profile persistence
private IBotOrchestrationService _orchestration;                 // Bot launch/runtime control
private readonly HttpClient _serverCatalogHttpClient;            // HTTP for server probes/catalog

// Related Core Classes (not injected, but used)
- BotSummaryItem (item in Bots collection)
- ServerSummaryItem (item in Servers collection)
- BotProfile, KnownServer, AgentRuntimeState (domain models)
- ServerAccessMetadata, ServerAccessMetadataResolver
- PluginDescriptor, ServerPluginCache, PluginDescriptor
```

### Internal State (Lockable Collections)

```csharp
private readonly object _serverProbeLock;                  // Prevents concurrent probes
private readonly object _serverCatalogRefreshLock;         // Cooldown tracking per server
private readonly object _deployConnectionLock;            // Active session tracking
private readonly object _activeAccessCacheLock;           // Active session metadata cache

private readonly Dictionary<string, DateTimeOffset> _serverCatalogLastRefreshUtc;
private readonly HashSet<string> _serverCatalogRefreshInFlight;
private readonly HashSet<(string BotId, string SessionId)> _activeDeployConnections;
private readonly Dictionary<(string, string), (RuntimeBotId, RuntimeBotName, ServerId, Access)> _activeSessionsByBotAndSession;
```

### Sub-System Integration Points

```
[MainWindowViewModel]
    │
    ├─→ [IClientStorage] (SQLite persistence layer)
    │   └─→ Bot profiles, known servers, plugin cache, runtime state
    │
    ├─→ [IBotOrchestrationService] (LocalBotOrchestrationService)
    │   └─→ Launch bots, interact with agent control sockets
    │
    ├─→ [IClientLogger] (event telemetry)
    │   └─→ Logs probe results, deploy outcomes, sessions, etc.
    │
    ├─→ [PersonaManager] (workspace file management)
    │   └─→ Create/rename/delete/load personas (conditional, non-core)
    │
    ├─→ [HttpClient] (HTTP for server catalog operations)
    │   └─→ GET /api/status, /api/game-catalog, /api/owner-token, /api/arenas
    │
    └─→ [Socket/Filesystem] (Unix domain socket control)
        └─→ Read agent control socket files in /tmp/bbs-agent-*.sock.control
```

---

## 4. DATA COLLECTIONS

### ObservableCollections (UI-Bound)

```csharp
public ObservableCollection<BotSummaryItem> Bots
    └─ Loaded from storage; excludes runtime instances
    └─ Populated by LoadBotsFromStorage()
    └─ Cleared on persona unload

public ObservableCollection<ServerSummaryItem> Servers
    └─ Loaded from storage with cached plugin counts
    └─ Populated by LoadServersFromStorage()
    └─ Updated after probe cycles

public ObservableCollection<ServerMetadataEntryItem> ServerMetadataEntries
    └─ From selected server's metadata dict
    └─ Displays key-value pairs in UI
    └─ Refreshed on server selection

public ObservableCollection<ServerPluginCatalogItem> ServerPluginCatalogEntries
    └─ Cached plugins from selected server's plugin cache
    └─ Populated from ServerPluginCache domain object
    └─ Updated after successful catalog fetch

public ObservableCollection<ServerArenaItem> ServerArenaEntries
    └─ Live active arenas fetched from server API
    └─ Polling every 900ms when in arena viewer context
    └─ Contains watch commands for each arena

public ObservableCollection<ActiveBotSessionItem> ActiveBotSessions
    └─ Currently deployed sessions for the selected bot
    └─ Reconciled from both storage and runtime sockets
    └─ Contains join/leave/quit commands per session
    └─ Arena options populated from ServerArenaEntries
```

### Private Collections (State/Caching)

```csharp
// Server probe/catalog state
private readonly Dictionary<string, DateTimeOffset> _serverCatalogLastRefreshUtc
    └─ Tracks when each server's catalog was last refreshed
    └─ Used to enforce 5-second cooldown (ServerCatalogSelectionRefreshCooldownMs)

private readonly HashSet<string> _serverCatalogRefreshInFlight
    └─ Tracks which servers have refresh requests in flight
    └─ Prevents duplicate concurrent refreshes

// Deployment connection tracking
private readonly HashSet<(string BotId, string SessionId)> _activeDeployConnections
    └─ Authoritative list of active bot-session pairs
    └─ Added when deploy handshake completes
    └─ Removed when session quits or disconnects

// Active session metadata cache
private readonly Dictionary<(string BotId, string SessionId), (string RuntimeBotId, string RuntimeBotName, string ServerId, ServerAccessMetadata Access)> _activeSessionsByBotAndSession
    └─ Maps (source_bot_id, session_id) → runtime metadata + server access
    └─ Populated during deploy and socket reconciliation
    └─ Pruned when socket files disappear (stale sessions)
```

### Selected/Current State Properties

```csharp
private BotSummaryItem? _selectedBot
    └─ Currently selected card in bot list
    └─ Triggers RefreshActiveBotSessionsProjection on change
    └─ Triggers server access metadata refresh

private ServerSummaryItem? _selectedServer
    └─ Currently selected card in server list
    └─ Triggers RefreshSelectedServerDetail on change
    └─ Used as fallback context for arena viewer

private WorkspaceContext _currentContext
    └─ Enum: Home | BotDetails | ServerDetails | ServerEditor | ArenaViewer
    └─ Drives which UI panels show
    └─ Modified by context-switching commands
```

---

## 5. COMMAND GROUPS & BINDINGS

### Context Navigation Commands

```csharp
ToggleLeftPanelCommand
    └─ Execution: ToggleLeftPanel() → IsLeftPanelExpanded = !IsLeftPanelExpanded
    └─ UI: Left sidebar collapse/expand

ToggleRightPanelCommand
    └─ Execution: ToggleRightPanel() → IsRightPanelExpanded = !IsRightPanelExpanded
    └─ UI: Right sidebar collapse/expand

SetHomeContextCommand
    └─ Execution: SetHomeContext() → SwitchWorkspaceContext(Home)
    └─ CanExecute: Always true

SetBotContextCommand
    └─ Execution: SetBotContextFromSelection() → Populate editor + switch context
    └─ CanExecute: SelectedBot is not null AND IsPersonaLoaded

SetServerContextCommand
    └─ Execution: SetServerContextFromSelection() → Populate editor + switch context
    └─ CanExecute: SelectedServer is not null AND IsPersonaLoaded
```

### Bot Management Commands

```csharp
StartNewBotCommand
    └─ Execution: StartNewBot() → Clear editor fields, set context to BotDetails
    └─ CanExecute: Always true

SaveBotProfileCommand
    └─ Execution: SaveBotProfile() → Validate + upsert to storage, reload UI
    └─ CanExecute: Always true (validation done in handler)

OpenBotEditorCommand: RelayCommand<BotSummaryItem>
    └─ Execution: OpenBotEditorFromCard(bot) → Select + populate + switch context
    └─ CanExecute: Always true (parameter guard in handler)

DeployBotFromCardCommand: RelayCommand<BotSummaryItem>
    └─ Execution: DeployBotFromCard(bot) → Select + execute deploy
    └─ CanExecute: bot is not null AND CanDeploySelectedBot()

DeploySelectedBotCommand
    └─ Execution: DeploySelectedBotToSelectedServer() → Full handshake + session setup
    └─ CanExecute: CanDeploySelectedBot() (persona loaded, bot+server selected, server live)
```

### Server Management Commands

```csharp
StartNewServerCommand
    └─ Execution: StartNewServer() → Clear editor, stop arena watch, set context
    └─ CanExecute: Always true

SaveServerProfileCommand
    └─ Execution: SaveServerProfile() → Validate port + upsert + reload + refresh metadata
    └─ CanExecute: Always true

OpenServerEditorFromCardCommand: RelayCommand<ServerSummaryItem>
    └─ Execution: OpenServerEditorFromCard(server) → Similar to bot editor
    └─ CanExecute: Always true

ActivateServerCardCommand: RelayCommand<ServerSummaryItem>
    └─ Execution: ActivateServerCardFromPanel(server) → Select or toggle to details
    └─ CanExecute: Always true

ReprobeServersCommand
    └─ Execution: ReprobeServers() → Run server probe cycle
    └─ CanExecute: !IsServerProbeInProgress AND Servers.Count > 0 AND IsPersonaLoaded
```

### Arena Management Commands

```csharp
RefreshServerArenasCommand
    └─ Execution: RefreshServerArenas() → Fetch live arena list from server API
    └─ CanExecute: SelectedServer is not null AND IsPersonaLoaded

CreateArenaCommand
    └─ Execution: ExecuteCreateArena() → POST to dashboard with owner token
    └─ CanExecute: CanExecuteOwnerTokenAction() (valid access + server + not loading)

JoinArenaCommand
    └─ Execution: ExecuteJoinArena() → POST to dashboard with owner token
    └─ CanExecute: CanExecuteOwnerTokenAction()

OpenArenaViewerInBrowserCommand
    └─ Execution: OpenArenaViewerInBrowser() → Process.Start(ArenaViewerUrl)
    └─ CanExecute: !string.IsNullOrWhiteSpace(ArenaViewerUrl)
```

### Active Session Commands

```csharp
// Created per-session in ActiveBotSessions collection:
JoinCommand (per ActiveBotSessionItem)
    └─ Execution: ExecuteSessionJoin() → Control socket /join_session
    └─ Payload: arena_id, handicap_percent

LeaveCommand (per ActiveBotSessionItem)
    └─ Execution: ExecuteSessionLeave() → Control socket /leave_session

QuitCommand (per ActiveBotSessionItem)
    └─ Execution: ExecuteSessionQuit() → Control socket /quit_session then disconnect
```

### Refresh/Background Commands

```csharp
RefreshServerAccessCommand
    └─ Execution: RefreshServerAccessMetadata() → Trigger async metadata load
    └─ CanExecute: Always true
    └─ Side Effect: Sets IsServerAccessLoading

RefreshServerAccessMetadataAsync() [internal]
    └─ Async task to resolve server access metadata
    └─ Tries bot's active sessions first, then known server metadata
    └─ Updates ServerAccessMetadata property
```

---

## 6. STATE MANAGEMENT PATTERNS

### Workspace Context Switching

```
Model: _currentContext: WorkspaceContext enum

Pattern:
1. Command calls SwitchWorkspaceContext(newContext)
2. StopArenaViewerWatch() if leaving ArenaViewer
3. _currentContext = newContext
4. RefreshContextProjection() propagates:
   └─ WorkspaceTitle, WorkspaceDescription
   └─ ShowBotEditor, ShowServerEditor, ShowServerDetails, ShowArenaViewer
   └─ CurrentTitleText derived property
   └─ Command CanExecute state changes
```

### Selection-Driven UI Updates

```
Bot Selection Flow:
SelectedBot = bot_item
    ↓
BotSummaryItem.PropertyChanged
    ↓
- RefreshActiveBotSessionsProjection() [load sessions for this bot]
- TriggerServerAccessRefresh() [update token access]
- RaiseCanExecuteChanged() on deploy commands
- RefreshActiveBotSessionsProjection() [reconcile sockets + storage]

Server Selection Flow:
SelectedServer = server_item
    ↓
ServerSummaryItem.PropertyChanged
    ↓
- RefreshSelectedServerDetail() [load metadata + plugins + arenas]
- RaiseCanExecuteChanged() on context commands
- TriggerServerAccessRefresh()
- PopulateServerEditor() [prefill form]
- SwitchWorkspaceContext(ServerDetails)
```

### Active Session Tracking (Complex)

```
Deployment Creates Session:
DeploySelectedBotToSelectedServer()
    ├─ Launch runtime instance (control socket created)
    ├─ Send server_connect → server_access handshake
    ├─ Receive session_id + owner_token + dashboard_endpoint
    ├─ Persist to storage (UpsertBotProfile + UpsertAgentRuntimeState)
    └─ Add to _activeDeployConnections + _activeSessionsByBotAndSession

Session Lookup Process:
RefreshActiveBotSessionsProjection()
    ├─ Call ReconcileActiveSessionsFromRuntimeSockets() [file-based recovery]
    │   └─ Scans /tmp/bbs-agent-*.sock.control files
    │   └─ Attempts to read "server_access" from each stale socket
    │   └─ **WARNING**: Assumes SelectedServer (can mismatch actual server)
    ├─ Call ReconcileActiveSessionsFromRuntimeProfiles() [storage-based]  
    │   └─ Scans storage for runtime instances with source_bot_id
    ├─ Build ActiveBotSessions collection from _activeDeployConnections
    └─ Populate arena options for each session

Session Cleanup:
PruneStaleActiveSessionCaches()
    ├─ Find sessions where control socket no longer exists
    └─ Remove from _activeDeployConnections + _activeSessionsByBotAndSession

Disconnect Flow:
DisconnectActiveDeploymentConnection(botId, sessionId, sendQuit)
    ├─ Optionally send "quit_session" control command
    ├─ Remove from _activeDeployConnections
    ├─ ClearActiveServerAccess(botId, sessionId)
    └─ RefreshActiveBotSessionsProjection()
```

### Server Probing & Caching Coordination

```
Startup Flow:
LoadPersonaAsync(filePath)
    └─ ReplaceRuntimeForPersonaAsync()
        └─ LoadBotsFromStorage() + LoadServersFromStorage()
        └─ OnLoadComplete: StartStartupServerProbe() [background]

Manual Probe:
ReprobeServersCommand
    └─ RunServerProbeCycleAsync(trigger: "manual", updateEditorStatus: true)

Probe Cycle:
RunServerProbeCycleAsync()
    ├─ TryBeginProbeCycle() [lock checks IsServerProbeInProgress]
    ├─ ProbeKnownServersAsync()
    │   └─ For each server: ProbeKnownServerWithRetryAsync() → ProbeKnownServerOnceAsync()
    │   └─ HTTP GET /api/status (with timeout + retry + N endpoint candidates)
    │   ├─ If reachable:
    │   │   ├─ Fetch plugin catalog → RefreshServerPluginCatalogCacheAsync()
    │   │   ├─ Fetch owner token → EnsureServerOwnerTokenAsync()
    │   │   └─ Update metadata: probe_status=reachable, probed_at_utc
    │   └─ If unreachable:
    │       └─ Update metadata: probe_status=unreachable, probe_error=<code>
    ├─ Upsert all updated servers to storage
    └─ EndProbeCycle() + UI dispatch

Catalog Refresh Coordination:
ShouldRefreshServerCatalog(serverId)
    ├─ Check: not already in-flight
    ├─ Check: not within 5-second cooldown
    └─ If OK: add to _serverCatalogRefreshInFlight + _serverCatalogLastRefreshUtc
```

### Async Operation Versioning Pattern

```
Used for: Arena refresh, server access refresh, server detail loading

Pattern:
private int _serverArenasRefreshVersion;

RefreshSelectedServerArenasAsync()
    ├─ refreshVersion = Interlocked.Increment(ref _serverArenasRefreshVersion)
    ├─ API call (can be slow)
    ├─ Check: IsArenaRefreshCurrent(refreshVersion, requestedServerId)
    │   └─ Compares refreshVersion == _serverArenasRefreshVersion
    │   └─ If false: Discard stale result (user selected different server)
    └─ Only update UI if current

Benefit: Prevents race conditions when user rapidly toggles between servers
```

### Server Access Metadata Resolution (Fallback Chain)

```
ResolveServerAccessMetadata(selectedServerId, selectedBotId):
    
    1st Try: Known Server Context (if ServerDetails view)
        └─ ResolveKnownServerAccessMetadata(selectedServerId)
            └─ Look up ClientOwnerTokenMetadataKey or ServerAccessOwnerTokenMetadataKey
            └─ Build dashboard endpoint via ResolveServerDashboardEndpoint()

    2nd Try: Active Session Context (if bot has active deployment)
        └─ TryGetActiveServerAccess(selectedBotId, selectedServerId)
            └─ Lookup in _activeSessionsByBotAndSession cache
            └─ Return (owner_token, dashboard_endpoint) from handshake

    3rd Try: First Attached Bot Fallback
        └─ Scan all bot profiles for one with IsAttached=true
        └─ Use its stored metadata

    Final: Delegate to ServerAccessMetadataResolver.Resolve()
        └─ Domain-level fallback logic

Uses for: Enable/disable owner token action buttons, populate token mask display
```

---

## 7. SYNCHRONIZATION & LOCKING STRATEGY

### Lock Usage

| Lock | Purpose | Hold Duration | Protected Data |
|------|---------|---|---|
| `_serverProbeLock` | Prevent concurrent probe cycles | Brief (boolean flip) | `_isServerProbeInProgress` |
| `_serverCatalogRefreshLock` | Cooldown + in-flight tracking | Brief | `_serverCatalogRefreshInFlight`, `_serverCatalogLastRefreshUtc` |
| `_deployConnectionLock` | Active session list modifications | Brief | `_activeDeployConnections` |
| `_activeAccessCacheLock` | Runtime session metadata cache | Brief | `_activeSessionsByBotAndSession` |

### Notable : UI Thread Affinity

```csharp
// Probe cycle results dispatched back to UI thread:
await Dispatcher.UIThread.InvokeAsync(() =>
{
    LoadServersFromStorage();
    TriggerServerAccessRefresh();
});

// Async operations scheduled on thread pool:
_ = Task.Run(async () => { RefreshSelectedServerArenasAsync(...); });

// Arena viewer polling loop:
_ = Task.Run(async () => { ... }, cts.Token);
```

---

## 8. CRITICAL ISSUES & ARCHITECTURAL CONCERNS

### 🔴 CRITICAL: Dual Session Reconciliation with Server Fallback Bug

**Location**: Lines 1881-1949 (socket reconciliation) vs. Lines 1844-1879 (storage reconciliation)

**Problem**:
- Two entirely different reconciliation paths exist for active sessions
- Socket-based fallback incorrectly assumes `SelectedServer?.ServerId` as the session's server
- If Session X runs on Server A but Server B is currently selected in UI, Session X incorrectly thinks it's on Server B
- Missing owner token and dashboard endpoint in socket fallback

**Read Session Memory**: Lines 4-42 in session memory for full analysis

---

### 🟠 HIGH PRIORITY: Suspicious Double Error Logging

**Location**: HandleOrchestrationException() method

**Problem**:
- Same deploy exception logged with two different event codes
- Marker `_mvfix` appended to error code but original also logged
- Dead giveaway of manual patch reconstruction

---

### 🟡 MEDIUM: Endpoint Building Logic Duplicated in 3 Places

**Locations**:
- BuildServerBaseEndpointCandidates() [ServerDiscovery.cs] - with scheme variants
- ResolveServerDashboardEndpoint() [MainWindowViewModel.cs] - single endpoint
- BuildAgentServerTargetEndpointCandidates() [MainWindowViewModel.cs] - bot-specific ports

**Problem**: Divergent logic for port selection, scheme handling, and fallback rules

---

### 🟡 MEDIUM: Unclear Active Session Source of Truth

**Problem**: 
- Sessions can come from storage reconciliation OR socket reconciliation
- Cache is populated from both, but merge semantics unclear
- No documentation on which source is authoritative
- Stale session cleanup only checks filesystem, not storage consistency

---

### 🟡 MEDIUM: Arena Viewer Dimension Logic Duplicated

**Locations**: StartWatchingArena() and UpdateArenaViewerFromArena()

**Pattern**: `Math.Max(300, viewerWidth > 0 ? viewerWidth : 760)` repeated

---

### 🟢 LOW: Metadata Keys Scattered Across Codebase

**Problem**: 
- Constants defined at top (ServerAccessServerIdMetadataKey, etc.)
- Magic strings used in code ("runtime_instance", "bot_port", etc.)
- Should be centralized in MainWindowViewModelHelpers

---

## 9. KEY WORKFLOWS

### Deploy Workflow

```
User: Click "Deploy" on bot card or button
    ↓
DeploySelectedBotToSelectedServer()
    ├─ Validation: Server must be Live, persona loaded
    ├─ Create runtime instance: BuildRuntimeInstanceProfile()
    │   └─ GUID suffix appended to name/ID
    │   └─ metadata: source_bot_id, runtime_instance, etc.
    ├─ Launch bot: _orchestration.LaunchBotAsync()
    │   └─ Control socket created in /tmp/
    ├─ Wait for control socket ready: WaitForControlSocketReady()
    ├─ Handshake:
    │   ├─ SendAgentControlRequest(server_connect)
    │   │   └─ Try multiple endpoint candidates
    │   └─ SendAgentControlRequest(server_access)
    │       └─ Get session_id, owner_token, dashboard
    ├─ Store:
    │   ├─ UpsertBotProfile(runtime instance profile + metadata)
    │   ├─ UpsertAgentRuntimeState (session state)
    │   └─ Add to _activeDeployConnections
    ├─ Cache:
    │   └─ SetActiveServerAccess() → _activeSessionsByBotAndSession
    ├─ Refresh UI:
    │   ├─ LoadBotsFromStorage()
    │   ├─ RefreshActiveBotSessionsProjection()
    │   └─ TriggerServerAccessRefresh()
    └─ Message: "Deployed X to server Y; active session established."
```

### Probe Workflow

```
Startup or Manual ReprobeServersCommand
    ↓
RunServerProbeCycleAsync()
    ├─ TryBeginProbeCycle() [acquire lock, set IsServerProbeInProgress=true]
    └─ For each known server:
        ├─ ProbeKnownServerOnceAsync() [max 2 attempts]
        │   ├─ GET /api/status to each endpoint candidate (http + https)
        │   └─ Return (isReachable, attempts, errorCode)
        ├─ If reachable:
        │   ├─ RefreshServerPluginCatalogCacheAsync()
        │   │   └─ GET /api/game-catalog → parse → UpsertServerPluginCacheAsync()
        │   └─ EnsureServerOwnerTokenAsync()
        │       └─ GET /api/owner-token → UpsertServerOwnerTokenMetadata()
        └─ Metadata: update probe_status, probe_last_checked, errors, etc.
    ├─ Dispatcher.UIThread.InvokeAsync():
    │   ├─ LoadServersFromStorage()
    │   └─ TriggerServerAccessRefresh()
    └─ EndProbeCycle() [release lock, set IsServerProbeInProgress=false]
```

### Arena Viewer Workflow

```
User: Select an arena from ServerArenaEntries
    ↓
StartWatchingArena(arenaId, game, viewerUrl, pluginEntryUrl, width, height)
    ├─ Set arena viewer label, URL, dimensions
    ├─ SwitchWorkspaceContext(ArenaViewer)
    └─ StartArenaViewerWatchLoop()
        └─ Task.Run(async):
            ├─ Every 900ms: RefreshSelectedServerArenasAsync(silent: true)
            │   └─ GET /api/arenas from all endpoint candidates
            │   └─ Find watched arena by ID
            │   └─ Call UpdateArenaViewerFromArena() if found
            │       └─ Update status, raw state, timestamps
            └─ Until CancellationToken or _watchedArenaId <= 0

User: Click "Open in Browser"
    ↓
OpenArenaViewerInBrowser()
    └─ Process.Start(ArenaViewerUrl) with UseShellExecute=true
```

### Session Control (Join/Leave/Quit)

```
User: Select arena from dropdown + click "JOIN"
    ↓
ExecuteSessionJoin(sourceBotId, sessionId)
    ├─ Get ActiveBotSessionItem from UI
    ├─ TryResolveRuntimeSessionForAction() [lookup in cache]
    │   └─ Get runtimeBotId from _activeSessionsByBotAndSession
    ├─ TrySendSessionControlRequest()
    │   └─ SendAgentControlRequest(join_session)
    │       └─ Payload: arena_id, handicap_percent
    ├─ Parse response for errors
    └─ Update BotEditorMessage status

Similarly for LEAVE:
ExecuteSessionLeave()
    └─ SendAgentControlRequest(leave_session)

And QUIT:
ExecuteSessionQuit()
    ├─ SendAgentControlRequest(quit_session)
    └─ DisconnectActiveDeploymentConnection(botId, sessionId)
        ├─ Remove from _activeDeployConnections
        ├─ ClearActiveServerAccess()
        └─ RefreshActiveBotSessionsProjection()
```

---

## 10. NOTABLE CONSTANTS & CONFIGURATION

```csharp
// Timeouts
ServerProbeTimeoutMs = 1200              // Per probe request
ServerCatalogFetchTimeoutMs = 2000       // HTTP client timeout
DeployHandshakeTimeoutMs = 3000          // Control socket handshake
DeployControlSocketReadyTimeoutMs = 8000 // Wait for control socket creation

// Retry/Rate Limiting
ServerProbeMaxAttempts = 2               // Retry failed probes
ServerProbeRetryDelayMs = 200            // Between attempts
ServerCatalogSelectionRefreshCooldownMs = 5000 // Min time between catalog refreshes
ArenaWatcherPollIntervalMs = 900         // Arena state polling interval

// Default Ports
DashboardPortFallback = 3000             // Dashboard (UI endpoint)
BotTcpDefaultPort = 8080                 // Agent control socket listener

// Metadata Key Strings
ServerAccessServerIdMetadataKey = "server_access.server_id"
ServerAccessSessionIdMetadataKey = "server_access.session_id"
ServerAccessOwnerTokenMetadataKey = "server_access.owner_token"
ServerAccessDashboardEndpointMetadataKey = "server_access.dashboard_endpoint"
ClientOwnerTokenMetadataKey = "client.owner_token"

// Metadata Key Search Chains
DashboardEndpointMetadataKeys = ["dashboard_endpoint", "server.dashboard_endpoint", ...]
DashboardPortMetadataKeys = ["dashboard_port", "server.dashboard_port"]
ServerGlobalIdMetadataKeys = ["global_server_id", "server.global_server_id", ...]
```

---

## 11. REFACTORING STRATEGY RECOMMENDATIONS

### Phase 1: Fix Critical Issues (High Impact)

1. **Merge dual session reconciliation** → Single authoritative path with proper server resolution
2. **Remove double error logging** → Pick one event code, consolidate logging

### Phase 2: Consolidate Duplicated Logic (Medium Effort)

3. **Extract endpoint builders** → Centralize into ServerEndpointResolver service
4. **Extract JSON parsing** → ServerResponseParser helper class
5. **Extract arena dimension logic** → ArenaViewerDimensionCalculator

### Phase 3: Organize Code (Low Effort)

6. **Centralize metadata keys** → MetadataKeyRegistry in helpers
7. **Group config constants** → ConfigurationDefaults class
8. **Document session reconciliation** → Add architecture comment block

---

## 12. SUMMARY TABLE

| Category | Count | Notes |
|---|---|---|
| ObservableCollections (public) | 5 | Bots, Servers, metadata, plugins, arenas |
| Private Collections (state) | 4 | Probe tracking, deploy tracking, cache |
| ICommand definitions | 16 | Navigation, bot, server, arena, session management |
| Async methods | 12+ | Probe, catalog, arena, session, persona |
| Lock objects | 4 | Probe, catalog, deploy, access cache |
| Partial files | 5 | Main + Personas + ServerDiscovery + ArenaWatcher + Types |
| Cyclomatic complexity concern | High | Nested conditions in reconciliation, deployment, handler selection |
| Test coverage gaps | Likely | Control socket handshake, session reconciliation, error paths |

---

**Generated**: April 7, 2026 | **Analysis Depth**: Comprehensive | **Ready for**: Refactoring planning
