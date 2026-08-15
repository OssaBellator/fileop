#!/usr/bin/env python3
"""Source inventory checks for #191 cross-volume Move pre-Copy native coverage."""
from __future__ import annotations

import argparse
import sys
from pathlib import Path


def require(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle in text, needle
    return len(needles)


def forbid(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle not in text, needle
    return len(needles)


def read(root: Path, relative: str) -> str:
    path = root / relative
    if not path.is_file():
        raise FileNotFoundError(str(path))
    return path.read_text(encoding="utf-8")


def check(root: Path) -> int:
    core = read(root, "src/FileOp.Core/Operations/FileCrossVolumeMoveSourcePreflight.cs")
    policy_tests = read(root, "tests/FileOp.Windows.Tests/FileCrossVolumeMoveSourcePreflightPolicyTests.cs")
    native = read(root, "tests/FileOp.Windows.Tests/FileCrossVolumeMoveSourcePreflightNativeTests.cs")
    fallback_native = read(root, "tests/FileOp.Windows.Tests/FileCrossVolumeMoveNativeTwoVolumeTests.cs")
    raw_delete = read(root, "src/FileOp.Windows/Operations/WindowsFileCrossVolumeMoveSourceDeletePrimitive.cs")

    checks = 0
    checks += require(
        core,
        "SourceProtectedLocation = 1 << 3",
        "FileCrossVolumeMoveProtectedLocationPreflightProbe",
        "_protectedLocationPolicy.Evaluate(",
        "request.CanonicalSourceDirectoryPath",
        "request.CanonicalSourcePath",
        "re-evaluated at the final source-delete capability boundary",
        "validation.DestinationDirectory.Identity is not FileIdentity destinationDirectoryIdentity",
        "sourceDirectoryIdentity.VolumeSerialNumber ==",
        "destinationDirectoryIdentity.VolumeSerialNumber",
        "SourcePreflightAuthorizesMutation => false",
    )

    checks += require(
        policy_tests,
        "SameVolumeMoveBypassesCrossVolumeSourcePreflight",
        "Assert.AreEqual(0, probe.CallCount)",
        "ProtectedSourceFileBlocksBeforeInnerEvidenceProbe",
        "SourceProtectedLocation",
        "Assert.AreEqual(0, inner.CallCount)",
        "AllowedProtectedLocationPolicyContinuesToInnerEvidenceProbe",
    )

    checks += require(
        native,
        '[TestCategory("CrossVolumeMoveNative")]',
        "SourceNamedStreamIsRefusedBeforeHistoryOrDestinationCopy",
        "SourceExtendedAttributeIsRefusedBeforeHistoryOrDestinationCopy",
        "ReadOnlySourceIsRefusedBeforeHistoryOrDestinationCopy",
        'Assert.AreEqual("CrossVolumeMoveValidationBlocked", result.Failure?.Code)',
        "Assert.IsFalse(File.Exists(DestinationPath(fixture, sourcePath)))",
        "Assert.IsNull(await history.GetAsync(plan.Id))",
        "FileCrossVolumeMovePreflightExecutionValidator",
        "FileCrossVolumeMoveProtectedLocationPreflightProbe",
        "new WindowsFileCrossVolumeMoveSourcePreflightProbe()",
        "new WindowsFileDeleteProtectedLocationPolicy()",
        "new WindowsFileOperationExecutionValidator()",
        "new WindowsFileCopyMutationPrimitive()",
        "new WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive()",
        "NtSetEaFile(",
        "FileAttributes.ReadOnly",
    )
    checks += forbid(native, "new WindowsMoveOperationExecutionValidator()")

    # Keep the older post-Copy refusal cases as defense-in-depth. If the early probe is
    # bypassed or source state changes later, the destructive capability path must still
    # refuse ADS/EA safely after the destination has committed.
    checks += require(
        fallback_native,
        "SourceNamedStreamRefusesDestructiveCompletionAndRetainsBothFiles",
        "SourceExtendedAttributeRefusesDestructiveCompletionAndRetainsBothFiles",
        'Assert.AreEqual("CrossVolumeMoveSourceDeletePreparationFailed", result.Failure?.Code)',
        "The already committed destination Copy should remain explicit",
    )

    # Early protected-location eligibility is UX/cost avoidance only. The real destructive
    # provider must retain its independent final policy evaluation.
    checks += require(
        raw_delete,
        "IFileDeleteProtectedLocationPolicy _protectedLocationPolicy",
        "EnsureSourceMutationAllowed(sourceRootPath, \"source directory\")",
        "EnsureSourceMutationAllowed(sourcePath, \"source file\")",
        "_protectedLocationPolicy.Evaluate(canonicalPath)",
        "protected from source deletion",
    )

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    args = parser.parse_args()
    count = check(args.repo_root.resolve())
    print(f"PASS cross-volume Move source-preflight native inventory: {count} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
