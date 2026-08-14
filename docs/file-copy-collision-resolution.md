# File Copy collision resolution

## Scope

Files supports three queued Copy collision policies: `Ask`, `Skip`, and `Stop`.

This document covers the user-facing resolution of an `Ask` plan after read-only preflight reports `NeedsDecision`. It does **not** define overwrite/replacement semantics.

## Immutable decision boundary

A queued plan is not edited in place when the user resolves an Ask-later collision.

The resolution flow is:

```text
existing Ask FileOperationPlan
-> exact read-only preflight == NeedsDecision
-> current pane/tab/path context still matches captured intent
-> explicit user choice: Skip existing OR Stop on collision
-> create a NEW FileOperationPlan with a fresh Guid
-> reuse the exact immutable FileOperationIntent snapshot
-> discard the old plan's preflight snapshot
-> require fresh preflight for the new plan
```

The fresh operation ID keeps durable-history single-use semantics unambiguous. The old preflight result describes the old `Ask` plan only and is never transferred as authorization to the resolved plan.

## Supported decisions

### Skip existing

`Skip` is non-destructive. During fresh execution-grade validation, an existing destination can be classified as `Skip`; the reviewed Copy executor records durable progress without invoking the mutation primitive for that entry.

A resolved Skip plan is still not executable until fresh preflight is `Ready` and the executor performs its own action-time canonical/identity validation.

### Stop on collision

`Stop` is fail-closed. If the collision still exists at fresh preflight/validation, the plan remains blocked and no mutation is attempted for that plan.

This is useful as an explicit decision to retain stop-on-conflict behavior rather than silently converting the plan to Skip.

## No overwrite or replacement

There is intentionally no `Replace` or `Overwrite` decision in this UI.

The production `WindowsFileCopyMutationPrimitive` uses exclusive destination creation. It is not an overwrite primitive, and the collision UI must not imply that it is one.

Any future replacement support requires a separate reviewed design covering at least:

- identity binding of the destination being replaced;
- durable history before destructive replacement begins;
- crash/recovery semantics for the old and new destination contents;
- cancellation boundaries;
- metadata/fidelity behavior;
- explicit user authorization for destructive replacement.

Until that contract exists, an Ask collision can resolve only to Skip or Stop.

## Source lifetime

Collision resolution is available only while the selected plan remains bound to the current Files source/destination pane, active tab, and directory paths.

A backing-source identity transition discards non-running queued plans. The collision decision therefore cannot migrate a textual path captured from one native/fallback source into another source context.

## Validation

`tools/verify_files_copy_collision_resolution.py` models the immutable resolution rules and pins the source wiring. It is included in `tools/test-local.ps1 -OfflineOnly`.

The complete Windows/.NET/WinUI/helper-process validation gate remains part of the batched release gate tracked by the beta roadmap and is not replaced by these source/model checks.
