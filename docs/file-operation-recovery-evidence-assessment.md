# Copy recovery evidence assessment

## Purpose

FileOp produces independent read-only recovery observations for a Copy-created file:

- destination-root canonical location and `FileIdentity`;
- destination leaf canonical location and `FileIdentity`;
- primary/default data-stream SHA-256;
- hard-link count;
- basic metadata owned by the Copy implementation;
- exact queried owner/group/DACL security-descriptor bytes, represented only by SHA-256;
- named `$DATA` stream topology/size evidence: exact stream names and logical sizes represented by a versioned SHA-256 digest plus count.

Those results intentionally remain separate because they describe different properties. This layer adds a **conservative aggregate assessment** so callers do not have to invent their own precedence rules or accidentally treat one matching dimension as deletion authority.

The assessor performs no filesystem access and no Windows API calls. It only combines already-produced Core result objects.

## Evidence dimensions

Each recovery entry exposes seven flags in `FileOperationRecoveryEvidenceDimension`:

1. `DestinationRoot`;
2. `DestinationIdentity`;
3. `MainStream`;
4. `HardLinkCount`;
5. `BasicMetadata`;
6. `OwnerGroupDacl`;
7. `NamedDataStreams`.

Every dimension is placed into exactly one state:

- `Matches` — the implemented evidence for that dimension matches its durable observation;
- `Changed` — the current observation definitively differs from the durable observation;
- `Incomplete` — durable history does not contain the evidence needed for that dimension;
- `Unavailable` — the evidence exists in principle, but the current state could not be verified reliably.

For `NamedDataStreams`, `Matches` means only that the observed named `$DATA` **names and logical sizes** match. It does not mean the bytes inside those named streams match. In particular, replacing ADS content with different bytes of the same length is intentionally invisible to this topology/size dimension.

The result keeps four non-overlapping dimension masks (`MatchingDimensions`, `ChangedDimensions`, `IncompleteDimensions`, `UnavailableDimensions`). Together they always partition `AllObserved`.

## Aggregate precedence

The per-entry aggregate is deliberately conservative:

1. any `Changed` dimension -> `ObservedEvidenceChanged`;
2. otherwise any `Unavailable` dimension -> `EvidenceUnavailable`;
3. otherwise any `Incomplete` dimension -> `EvidenceIncomplete`;
4. otherwise -> `ObservedSubsetMatches`.

This means a known difference is never hidden by a simultaneous read error or missing legacy evidence. An inability to verify current state is also not downgraded to merely missing evidence.

## Input binding

`FileOperationRecoveryEvidenceAssessor` accepts exactly one:

- `FileOperationRecoveryInspection`;
- `FileOperationRecoveryContentVerification`;
- `FileOperationRecoveryBasicMetadataVerification`;
- `FileOperationRecoverySecurityDescriptorVerification`;
- `FileOperationRecoveryNamedDataStreamTopologyVerification`.

All five must belong to the same operation and contain the exact same unique entry ordinals. Every verification item must point back to the same recovery-inspection snapshot for that ordinal. Mismatched operation ids, duplicate/missing ordinals, or a stale/fabricated inspection item are rejected rather than aggregated.

Output is sorted by ordinal and defensively snapshotted.

## What `ObservedSubsetMatches` means

`ObservedSubsetMatches` means only:

> every evidence dimension currently implemented by this assessor matched its durable observation at the points where those independent observations were taken.

It is **not** an “unchanged file” result.

In particular it still does not prove equality of:

- named-data-stream **contents** when names and logical sizes are unchanged;
- extended attributes;
- the complete set of hard-link names (a link count is not a name inventory);
- SACL/audit state or other privileged security-information classes;
- any property not represented by the seven current evidence dimensions.

It also cannot prove historical absence of temporary changes that were later restored. The individual observations are point-in-time evidence and are not one atomic file snapshot.

## Safety boundary

This assessment layer does not:

- read or mutate the filesystem;
- change `UndoKind`;
- add `CanDelete` or `CanUndo`;
- delete, replace, move, rename, or recover a destination;
- alter owner/group/DACL, named streams, or any other metadata;
- authorize Files UI execution or Undo.

Even `ObservedSubsetMatches` grants **no mutation authority**.

A future destructive recovery boundary must define the still-missing evidence/policy, require explicit user authorization, and freshly revalidate the final policy using handles held through the actual authorized relative mutation.

## Validation

The zero-Actions verifier `tools/verify_recovery_evidence_assessment.py` models the seven dimension states and aggregate precedence over randomized cases and source-guards:

- the complete seven-dimension partition;
- changed > unavailable > incomplete > observed-subset-match precedence;
- same-operation/same-ordinal/same-inspection binding;
- defensive sorted snapshots;
- absence of filesystem and mutation APIs;
- explicit documentation that named-stream matching is names/sizes only and `ObservedSubsetMatches` is not an unchanged-file or deletion-authorization claim.

The verifier is wired into the portable Copy wrappers. The focused Windows test gate includes both aggregate assessment test classes; compiler execution can be batched with the other stacked draft recovery work.