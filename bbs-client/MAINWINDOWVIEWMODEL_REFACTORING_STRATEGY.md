# MainWindowViewModel Refactoring Strategy

## Executive Summary

**Current State**: MainWindowViewModel is a God Object (~2900 LOC) combining 10 responsibility areas across 5 files (1 main + 4 partials).

**Target State**: Decompose into 5-6 focused, independently-testable service ViewModels that MainWindowViewModel **orchestrates** rather than **contains**.

**Key Benefit**: Reduced cognitive load, testability, independent evolution, and clear separation of concerns.

### Status Checkpoint (2026-04-07)

Refactor progress is materially ahead of the original week-1 plan.

Completed and validated:
- UI state extraction is in place (UIStateViewModel integration).
- Server management extraction is in place (ServerServiceViewModel integration).
- Arena editor/state extraction is in place (ArenaServiceViewModel integration).
- Session/access concerns are partially extracted (SessionServiceViewModel and ServerAccessServiceViewModel are integrated).
- Major UI regressions discovered during integration (server form visibility, server list rebinding, owner-token action gating, arena refresh command behavior, and plugin-default args hydration) were patched and revalidated in the app.

Current blocker to a "fully green" refactor checkpoint:
- Full bbs-client suite run shows 4 failing tests in MainWindowViewModelActiveSessionResolutionTests (reflection-based field wiring assumptions no longer match current composition).

Realigned immediate objective:
- Stabilize/modernize the failing active-session resolution tests so test coverage matches the new service-composition architecture.

---

## Part 1: Current Responsibilities Inventory

### Domain 1: Bot Management
**Current Location**: `MainWindowViewModel.BotWatcher.cs` (partial)
- Bot list (ObservableCollection persistence)
- Bot selection and detail projection
- Bot editor form state
- Bot deployment coordination
- Deploy connection lifecycle

**Concerns**: Bot probing, metadata reading, deployment orchestration, active session tracking

### Domain 2: Server Management
**Current Location**: `MainWindowViewModel.ServerWatcher.cs` (partial)
- Server list persistence
- Server selection/detail projection
- Server editor form state
- Server probing (reachability + plugin catalog)
- Server metadata caching
- Metadata key resolution

**Concerns**: Network probing, caching strategies, API fallback chains, metadata key mapping

### Domain 3: Arena Management
**Current Location**: `MainWindowViewModel.ArenaWatcher.cs` (partial)
- Arena list for selected server
- Arena creation/joining
- Arena viewer browser launch
- Arena polling (900ms interval)

**Concerns**: Async polling, stale-data prevention (versioning), deduplication, lifecycle management

### Domain 4: Session Orchestration & Control
**Current Location**: `MainWindowViewModel.cs` (main file, scattered)
- Active bot session tracking (from deployments)
- Session reconciliation (storage vs. filesystem vs. runtime)
- Session control (join/leave/quit)
- Server access metadata caching per session
- Owner token resolution

**Concerns**: State reconciliation across multiple sources, timeout handling, connection loss recovery

### Domain 5: UI State & Navigation
**Current Location**: `MainWindowViewModel.cs` (main file)
- WorkspaceContext enum handling
- Left/Right panel collapse state
- Visibility bindings (ShowBotEditor, ShowServerEditor, etc.)
- Context switching logic
- Window title projection

**Concerns**: State machines for UI contexts, visibility coordination

### Domain 6: Deployment Workflow
**Current Location**: Scattered across files
- CompareAndSwapDeploy logic
- Agent control socket communication
- Plugin manifest validation
- Deployment status tracking
- Deployment error handling

**Concerns**: Complex state machine, socket communication, plugin compliance

### Domain 7: Owner-Token & Server Access
**Current Location**: `MainWindowViewModel.cs` (main file, scattered)
- Server access metadata resolution
- Dashboard endpoint building (3 different methods!)
- Owner token fallback chains
- Multiple metadata key lookups

**Concerns**: Endpoint building duplication, metadata key constants scattered, unclear source-of-truth

### Other Minor Domains
- **Persona management**: Loading/storing user preferences
- **Logging & telemetry**: IClientLogger usage throughout
- **HTTP client management**: Server catalog fetching

---

## Part 2: Proposed Component Architecture

### Tier 1: Core Service ViewModels (Independent, Testable)

#### **1. BotServiceViewModel**
```
Responsibilities:
  - Bot list persistence & updates
  - Bot selection management
  - Bot editor form state (name, launch_path, args, metadata)
  - Bot editor save/create/delete
  - Deploy command orchestration (delegates to SessionServiceViewModel)

Properties:
  - Bots: ObservableCollection<BotSummaryItem>
  - SelectedBot: BotSummaryItem?
  - BotEditorName, BotEditorLaunchPath, BotEditorArgs, BotEditorMetadata
  - BotEditorMessage
  - Commands: SaveBotProfileCommand, StartNewBotCommand, DeployBotFromCardCommand

Dependencies:
  - IClientStorage (bot persistence)
  - IClientLogger

Isolation Benefits:
  - Can be tested independently with mock storage
  - Zero coupling to server/arena/session logic
  - Deployable in different contexts (could reuse in separate bot manager)
```

#### **2. ServerServiceViewModel**
```
Responsibilities:
  - Server list persistence & updates
  - Server selection management
  - Server editor form state (name, host, port, tls, metadata)
  - Server editor save/create/delete
  - Server probing orchestration (reachability check)
  - Server metadata caching logic
  - Plugin catalog fetching
  - Metadata key resolution (all 3 methods consolidated)

Properties:
  - Servers: ObservableCollection<ServerSummaryItem>
  - SelectedServer: ServerSummaryItem?
  - ServerEditorName, ServerEditorHost, ServerEditorPort, ServerEditorUseTls, ServerEditorMetadata
  - ServerEditorMessage
  - IsServerProbeInProgress
  - ServerPluginCatalogEntries: ObservableCollection<ServerPluginCatalogItem>
  - ServerMetadataEntries: ObservableCollection<ServerMetadataEntryItem>
  - Commands: SaveServerProfileCommand, StartNewServerCommand, ReprobeServersCommand

Dependencies:
  - IClientStorage (server persistence)
  - IClientLogger
  - HttpClient (catalog fetching)

Isolation Benefits:
  - Server probing logic fully encapsulated
  - Metadata caching is independent concern
  - Can test probing with mock HTTP client
  - Plugin catalog updates isolated from bot/arena logic
```

#### **3. ArenaServiceViewModel**
```
Responsibilities:
  - Arena list for selected server
  - Arena creation workflow
  - Arena joining workflow
  - Arena polling (900ms interval) with refresh versioning
  - Arena viewer browser launch
  - Stale-data prevention (versioning + deduplication)

Properties:
  - ServerArenaEntries: ObservableCollection<ServerArenaItem>
  - OwnerArenaSelectedPlugin, OwnerArenaArgs, OwnerArenaTimeMs, OwnerArenaAllowHandicap
  - OwnerJoinArenaId, OwnerJoinHandicapPercent
  - Commands: CreateArenaCommand, JoinArenaCommand, RefreshServerArenasCommand, OpenArenaViewerInBrowserCommand

Dependencies:
  - IClientLogger

Isolation Benefits:
  - Polling loop encapsulated
  - Versioning logic isolated (no interference from session changes)
  - Can be tested without server probing
  - State transitions testable
```

#### **4. SessionServiceViewModel**
```
Responsibilities:
  - Active bot session tracking
  - Session reconciliation (storage ↔ storage + filesystem ↔ runtime)
  - Session state projection (join/leave/quit ↔ command transitions)
  - Server access metadata per-session (owner token, dashboard endpoint)
  - Owner-token action availability logic
  - Session control (join/leave/quit commands)

Properties:
  - ActiveBotSessions: ObservableCollection<ActiveBotSessionItem>
  - HasActiveBotSessions, ShowActiveBotSessionsEmpty
  - ServerAccessMetadata: ServerAccessMetadata (for control actions)
  - ServerAccessOwnerToken, ServerAccessDashboardEndpoint
  - OwnerTokenActionStatus
  - Commands: CreateArenaCommand (via ServerAccess), JoinArenaCommand, etc.

Dependencies:
  - IClientStorage
  - IBotOrchestrationService (runtime session queries)
  - IClientLogger

Isolation Benefits:
  - Reconciliation logic separate from bot/server logic
  - Session lifecycle testable in isolation
  - Owner token chain fully encapsulated
```

#### **5. UIStateViewModel**
```
Responsibilities:
  - WorkspaceContext enum handling
  - Left/Right panel expand/collapse state
  - Visibility properties (ShowBotEditor, ShowServerEditor, etc.)
  - CurrentContextLabel, CurrentTitleText projection
  - Context switching orchestration

Properties:
  - CurrentContext: WorkspaceContext
  - IsLeftPanelExpanded, IsRightPanelExpanded
  - ShowBotEditor, ShowServerEditor, ShowServerDetails, ShowArenaViewer
  - LeftPanelWidth, RightPanelWidth (computed)
  - CurrentContextLabel, CurrentTitleText (computed)
  - Commands: ToggleLeftPanelCommand, ToggleRightPanelCommand, SetHomeContextCommand, SetBotContextCommand, SetServerContextCommand

Dependencies:
  - IClientStorage (panel state persistence)
  - IClientLogger

Isolation Benefits:
  - UI state machine testable independently
  - Context switching logic isolated
  - Can be reused in different main windows
```

#### **6. DeploymentServiceViewModel** (Optional, High Complexity)
```
Responsibilities:
  - CompareAndSwapDeploy orchestration
  - Agent control socket communication
  - Plugin manifest validation
  - Deployment handshake & timeout coordination
  - Control socket path building
  - Deployment error classification

Properties:
  - DeploymentInProgress: bool
  - DeploymentErrorMessage: string
  - Commands: DeploySelectedBotCommand (internally uses bot/server/session services)

Dependencies:
  - IClientStorageService (session metadata)
  - IClientLogger

Isolation Benefits:
  - Complex deployment state machine isolated
  - Socket communication logic testable
  - Error handling and retry logic centralized
  - Testable with mock sockets

**Note**: This is optional for Phase 1; can be extracted in Phase 2 if MainWindowViewModel still feels large.
```

---

### Tier 2: Composition Root (MainWindowViewModel)

#### **Refactored MainWindowViewModel**
```
Responsibility: **Orchestration only** (no data storage or business logic)
  - Inject all service ViewModels
  - Coordinate cross-cutting concerns
  - Expose composite commands that span service ViewModels
  - Manage dependencies between services (e.g., "when server selected, clear arenas")
  - Expose workspace-level queries that span services

Public Interface:
  - BotService: BotServiceViewModel { get; }
  - ServerService: ServerServiceViewModel { get; }
  - ArenaService: ArenaServiceViewModel { get; }
  - SessionService: SessionServiceViewModel { get; }
  - UIState: UIStateViewModel { get; }
  - DeploymentService: DeploymentServiceViewModel { get; }

  // Composite commands that touch multiple services:
  - SelectBotCommand → BotService + UIState.SetBotContext
  - SelectServerCommand → ServerService + ArenaService.Clear + UIState.SetServerContext
  - ProbeSelectedServerCommand → ServerService.Probe + (if success) SessionService.RefreshMetadata
  - DeploySelectedBotCommand → DeploymentService + SessionService.TrackSession

  // Factory methods for XAML binding convenience:
  - GetArenaOptionsForServer(serverId) → delegates to ArenaService
  - GetServerName(serverId) → lookup in ServerService

Size Estimate: ~200-300 LOC (vs. current ~2900)
```

#### **XAML Integration**
```
<Window x:Class="Bbs.Client.App.MainWindow"
        DataContext="{Binding MainWindowViewModel}">
  
  <!-- Each service exposes its own namespace for better XAML clarity -->
  <local:BotPanel DataContext="{Binding BotService}" />
  <local:ServerPanel DataContext="{Binding ServerService}" />
  <local:ArenaPanel DataContext="{Binding ArenaService}" />
  <local:SessionPanel DataContext="{Binding SessionService}" />
  
  <!-- UIState managed centrally -->
  <DockPanel>
    <Grid Visibility="{Binding UIState.ShowBotEditor, Converter={...}}">...</Grid>
    <Grid Visibility="{Binding UIState.ShowServerEditor, Converter={...}}">...</Grid>
  </DockPanel>
</Window>
```

---

## Part 3: Refactoring Phases

### Phase 1: Extraction (Low Risk, High Clarity Gain)
**Estimated Effort**: 20-30 hours over 2-3 weeks

1. **Extract UIStateViewModel** (easiest, most independent)
   - Move WorkspaceContext, panel state, visibility logic
   - Move ToggleLeftPanelCommand, etc.
   - Inject into MainWindowViewModel
   - Update XAML bindings to `DataContext.UIState.*`
   - No breaking changes to existing behavior

2. **Extract BotServiceViewModel** (low coupling to others)
   - Move bot list, editor state, Save/Create/Delete logic
   - Keep Deploy logic for now (Phase 2)
   - Inject into MainWindowViewModel
   - Update XAML bindings
   - Tests: Mock IClientStorage, verify bot persistence

3. **Extract ServerServiceViewModel** (isolated concern)
   - Move server list, editor state, probing logic
   - Move metadata and plugin catalog fetching
   - **Consolidate 3 endpoint building methods → single ServerEndpointResolver class**
   - Inject into MainWindowViewModel
   - Tests: Mock storage & HttpClient, verify caching behavior

4. **Extract UIStateViewModel Coordination**
   - Add MainWindowViewModel coordination: server selection → clear arenas
   - Context switching still orchestrated in MainWindowViewModel

**Validation**: Build passes, existing tests pass, no UI regression

### Phase 2: Session & Arena Services (Medium Complexity)
**Estimated Effort**: 15-20 hours

5. **Extract ArenaServiceViewModel**
   - Move arena list, creation, joining, polling
   - Move versioning logic
   - Inject into MainWindowViewModel
   - Tests: Verify versioning prevents race conditions, deduplication works

6. **Extract SessionServiceViewModel** (complex reconciliation)
   - Move active session tracking, reconciliation logic
   - Move server access metadata & owner token resolution
   - **FIX CRITICAL BUG**: Session reconciliation currently assumes SelectedServer as fallback (line ~1920)
   - Inject into MainWindowViewModel
   - Tests: Mock both storage & orchestration service, verify reconciliation logic

7. **Cross-Service Coordination**
   - MainWindowViewModel now coordinates selection cascades:
     - ServerSelection → ArenaService.SetServer + SessionService.RefreshMetadata
     - BotSelection → SessionService.FilterSessions
   - Tests: Verify state coordination doesn't deadlock

**Validation**: Build passes, all tests pass including session reconciliation edge cases

### Phase 3: Deployment Service (Optional, High Risk/High Reward)
**Estimated Effort**: 10-15 hours

8. **Extract DeploymentServiceViewModel** (most complex)
   - Move CompareAndSwapDeploy orchestration
   - Move socket communication logic into helpers
   - Move plugin manifest validation
   - Inject into MainWindowViewModel
   - Tests: Mock all socket operations, verify handshake state machine

9. **Reduce MainWindowViewModel to Pure Orchestration**
   - MainWindowViewModel becomes orchestration coordinator
   - Commands delegate to service ViewModels
   - Factory methods for XAML convenience only
   - ~200 LOC remaining

**Validation**: Build passes, deployment tests isolated and verifiable

---

## Part 4: Architectural Benefits

### Immediate (Phase 1)
✅ 50% reduction in cognitive load (UIStateViewModel extracted)  
✅ BotServiceViewModel testable without side effects  
✅ ServerServiceViewModel probing testable independently  

### Medium-term (Phase 2)
✅ Session reconciliation logic isolated and debuggable  
✅ Arena versioning logic provable via unit tests  
✅ Clear boundaries prevent cross-concern bugs  

### Long-term (Phase 3)
✅ Deployment workflow testable without deployment attempts  
✅ New features can target specific service (e.g., new bot format → only BotServiceViewModel)  
✅ Potential to extract services to separate libraries (e.g., `Bbs.Client.SessionManagement`)  

---

## Part 5: Dependency Graph (Proposed)

```
┌────────────────────────────────────────────────────┐
│         MainWindowViewModel (Orchestrator)         │
│  - Composes all service ViewModels                │
│  - Cross-service coordination                      │
│  - Command delegation                              │
│  Size: ~250 LOC (vs. 2900 current)                │
└──┬──────┬──────┬──────┬──────┬──────────────────┘
   │      │      │      │      │
   ↓      ↓      ↓      ↓      ↓
┌──────┐┌────┐┌──────┐┌──────┐┌────────────┐
│Bot   ││Srv ││Arena ││Sess  ││UIState     │
│Svc   ││Svc ││Svc   ││Svc   ││Vm          │
│      ││    ││      ││      ││            │
│~400  ││~500││~300  ││~600  ││~150 LOC   │
│LOC   ││LOC ││LOC   ││LOC   ││           │
└──┬───┘└──┬─┘└──┬───┘└──┬───┘└───┬────────┘
   │       │     │       │        │
   └───────┴─────┴───┬───┴────────┘
                     │
          ┌──────────┤
          ↓          ↓
     Storage    Orchestration
     Logger     HttpClient
     
```

---

## Part 6: Concrete First Steps (This Week)

### Step 1: Create UIStateViewModel
**File**: `ViewModels/UIStateViewModel.cs`
```csharp
public class UIStateViewModel : ViewModelBase
{
    private WorkspaceContext _currentContext = WorkspaceContext.Home;
    private bool _isLeftPanelExpanded = true;
    private bool _isRightPanelExpanded = true;
    
    public WorkspaceContext CurrentContext { get; set; }
    public bool IsLeftPanelExpanded { get; set; }
    public ICommand ToggleLeftPanelCommand { get; }
    // ... (move all UI state logic)
}
```

**Changes to MainWindowViewModel**:
```csharp
public class MainWindowViewModel
{
    private UIStateViewModel _uiState;  // New
    public UIStateViewModel UIState => _uiState;
    
    // Remove these (now in UIStateViewModel):
    // - _currentContext
    // - _isLeftPanelExpanded
    // - _isRightPanelExpanded
    // - ToggleLeftPanelCommand, etc.
    // - CurrentContextLabel, ShowBotEditor properties
}
```

**XAML Changes**:
```xaml
<!-- Before -->
<Grid Visibility="{Binding ShowBotEditor, Converter={...}}">

<!-- After -->
<Grid Visibility="{Binding UIState.ShowBotEditor, Converter={...}}">
```

**Impact**: ~200 LOC removed from MainWindowViewModel, zero functional change

### Step 2: Create ServerEndpointResolver Helper
**File**: `ViewModels/Helpers/ServerEndpointResolver.cs`
```csharp
public class ServerEndpointResolver
{
    // Consolidate 3 methods:
    // - NormalizeDashboardEndpoint(raw, host, port, tls)
    // - ExtractDashboardEndpoint(metadata)
    // - BuildProbeUrl(host, port, tls)
    
    public static string Resolve(string? raw, string host, int port, bool useTls)
    {
        // Single source of truth for endpoint building
    }
}
```

**Why**: Fixes critical issue of 3 divergent methods building endpoints inconsistently.

### Step 3: Create BotServiceViewModel Skeleton
**File**: `ViewModels/Services/BotServiceViewModel.cs`
```csharp
public class BotServiceViewModel : ViewModelBase
{
    private readonly IClientStorage _storage;
    private readonly IClientLogger _logger;
    
    public ObservableCollection<BotSummaryItem> Bots { get; }
    public BotSummaryItem? SelectedBot { get; set; }
    
    public string BotEditorName { get; set; }
    public string BotEditorLaunchPath { get; set; }
    // ... move all bot-related properties
    
    public ICommand SaveBotProfileCommand { get; }
    // ... move all bot commands (except Deploy)
}
```

---

## Part 7: Why This Works

### Testability
- **Before**: Testing bot save required mocking entire MainWindowViewModel (2900 LOC, multiple concerns)
- **After**: Testing bot save requires mocking only IClientStorage (clean, focused)

### Maintainability
- **Bug in session reconciliation**: Look in SessionServiceViewModel (~600 LOC) instead of searching 2900 LOC
- **Feature: New bot format support**: Touch only BotServiceViewModel, zero changes to server/arena/session logic

### Cognitive Load
- **Developer reading bot list UI**: Follows BotServiceViewModel bindings (400 LOC) not entire MainWindowViewModel
- **Code review**: PR that extracts UIStateViewModel is easy to verify (no logic changes, just organization)

### Code Reuse
- **Desktop + Mobile UI**: Both could share BotServiceViewModel, ServerServiceViewModel via shared library
- **Separate "Manage Sessions" tool**: Could reuse SessionServiceViewModel directly

---

## Part 8: Risk Mitigation

| Risk | Mitigation |
|------|-----------|
| Breaking XAML bindings during extraction | Create new properties in MainWindowViewModel that delegate to services (temporary bridge) |
| Cross-service state not coordinating | MainWindowViewModel handles all coordination; create integration tests for cascades (e.g., server selected → arenas cleared) |
| Performance regression (more ViewModels) | No GC pressure; all services created once at startup; Obersvables unchanged |
| Existing tests failing | Phase 1 extracts UI state only (no logic); tests should pass unchanged. Phase 2+ tests are additive. |

---

## Recommendation

**Realigned next step**: Complete test-suite realignment for active-session resolution scenarios.

**Why this now?**
1. ✅ Architecture extraction is already in place; test debt is now the gating risk.
2. ✅ The 4 failing tests are concentrated in one test class with reflection assumptions, making scope bounded.
3. ✅ Green tests are needed before additional decomposition (especially DeploymentService extraction) to avoid compounding uncertainty.
4. ✅ Fixing these tests creates a stable baseline for any final MainWindowViewModel slimming.

**Success metric**: bbs-client solution tests pass fully, then proceed to optional Phase 3 extraction with confidence.
