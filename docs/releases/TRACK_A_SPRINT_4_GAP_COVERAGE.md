# Track A Sprint 4 Gap Coverage

Purpose:
- Capture behavior/usability gaps discovered during Sprint 4 implementation that were not explicit in the original checklist.
- Track closure work that must happen before packaging and alpha readiness.

Status model:
- `todo`: identified and not started
- `in-progress`: actively being addressed
- `blocked`: needs external decision/dependency
- `done`: implemented and validated

Priority model:
- `P0`: blocks basic feature usage
- `P1`: significant workflow/usability issue
- `P2`: quality polish or low-risk follow-up

## Gate Before PR5

Do not begin packaging/readiness work until all `P0` gaps are `done` and at least one manual verification pass is recorded in the Validation Log section.

## Gap Backlog

| Gap ID | Priority | Area | Scope | Actionable coverage | Status |
| --- | --- | --- | --- | --- | --- |
| G4-S1 | P0 | Server interaction | Plugin catalog must come from server probe results, not manual entry. | Implement probe path that fetches plugin catalog and metadata; persist as last-known server cache; refresh cache when selected server is populated in center panel. | todo |
| G4-B1 | P0 | Bot interaction | Bot summary card behavior should drive core workflow. | Remove entry/state text from bot cards, add arm/disarm toggle on bot summary card, add deploy button on bot summary card gated by armed bot plus selected server in center panel; deploy action must attach agent and establish active session with owner token. | todo |

## Coverage Plan Template (Per Gap)

For each gap, add a subsection using this template:

### <Gap ID>: <Short title>

- Scope:
- Design notes:
- Code touch points:
- Tests to add/update:
- Manual verification steps:
- Exit criteria:

## Validation Log

Record evidence entries as work closes gaps.

| Date | Gap ID | Validation type | Evidence | Result |
| --- | --- | --- | --- | --- |
| TBD | TBD | unit/integration/manual | TBD | pending |

## Decisions Needed

Capture product/engineering decisions required to unblock gap closure.

| Decision ID | Topic | Options | Recommended | Needed by | Status |
| --- | --- | --- | --- | --- | --- |
| D4-S1 | Probe payload contract | Existing plugin metadata shape vs expanded metadata contract from server | TBD | before G4-S1 implementation | open |
| D4-B1 | Deploy trigger behavior | Synchronous attach command vs async task flow with progress state | TBD | before G4-B1 implementation | open |

## Suggested Execution Order

1. Implement G4-S1 server probe-driven plugin catalog and cache refresh behavior.
2. Implement G4-B1 bot summary card interaction changes (toggle plus deploy gating and attach flow).
3. Run manual validation against both gaps and record evidence in Validation Log.
4. Resume Sprint 4 PR5 packaging/readiness only after gate conditions are met.
