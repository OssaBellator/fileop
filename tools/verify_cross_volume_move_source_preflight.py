#!/usr/bin/env python3
"""Zero-Actions source/model checks for cross-volume Move source preflight."""
from __future__ import annotations

import argparse
import random
from pathlib import Path


def require(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle in text, needle
    return len(needles)


def reject(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle not in text, needle
    return len(needles)


def model_preflight(attributes: int, streams: int, ea_size: int) -> tuple[bool, set[str]]:
    read_only = 0x00000001
    normal = 0x00000080
    stable_copied = 0x00000001 | 0x00000002 | 0x00000004 | 0x00000020 | 0x00002000
    allowed = (stable_copied & ~read_only) | normal
    blockers: set[str] = set()
    if attributes & ~allowed:
        blockers.add("SourceUnsupportedAttributes")
    if streams:
        blockers.add("SourceNamedDataStreams")
    if ea_size:
        blockers.add("SourceExtendedAttributes")
    return not blockers, blockers


def check_model(cases: int) -> int:
    checks = 0
    allowed, blockers = model_preflight(0x20, 0, 0)
    assert allowed and not blockers
    checks += 2

    allowed, blockers = model_preflight(0x1, 1, 16)
    assert not allowed
    assert blockers == {
        "SourceUnsupportedAttributes",
        "SourceNamedDataStreams",
        "SourceExtendedAttributes",
    }
    checks += 2

    rng = random.Random(20260815)
    known_bits = [0x1, 0x2, 0x4, 0x20, 0x80, 0x2000, 0x400, 0x800, 0x4000]
    for _ in range(cases):
        attributes = 0
        for bit in known_bits:
            if rng.getrandbits(1):
                attributes |= bit
        streams = rng.randrange(0, 3)
        ea_size = rng.choice([0, 0, 0, 8, 32])
        allowed, blockers = model_preflight(attributes, streams, ea_size)
        assert allowed == (len(blockers) == 0)
        if attributes & 0x1:
            assert "SourceUnsupportedAttributes" in blockers
        if streams:
            assert "SourceNamedDataStreams" in blockers
        if ea_size:
            assert "SourceExtendedAttributes" in blockers
        checks += 1 + int(bool(attributes & 0x1)) + int(bool(streams)) + int(bool(ea_size))
    return checks


def check_repository(root: Path) -> int:
    paths = {
        "core": root / "src/FileOp.Core/Operations/FileCrossVolumeMoveSourcePreflight.cs",
        "windows": root / "src/FileOp.Windows/Operations/WindowsFileCrossVolumeMoveSourcePreflightProbe.cs",
        "tests": root / "tests/FileOp.Windows.Tests/FileCrossVolumeMoveSourcePreflightTests.cs",
        "fidelity": root / "src/FileOp.Core/Operations/FileCrossVolumeMoveFidelity.cs",
    }
    source: dict[str, str] = {}
    for key, path in paths.items():
        if not path.is_file():
            raise FileNotFoundError(path)
        source[key] = path.read_text(encoding="utf-8")

    checks = 0
    core = source["core"]
    checks += require(
        core,
        "FileCrossVolumeMoveSourcePreflightClassifier",
        "FileCrossVolumeMovePreflightExecutionValidator",
        "IFileCrossVolumeMoveSourcePreflightProbe",
        "SourcePreflightAuthorizesMutation => false",
        "FileOperationExecutionValidationDecision.Blocked",
        "FileOperationExecutionValidationStatus.Blocked",
        "before durable history or destination Copy",
        "FileBasicMetadataEvidence.StableCopiedAttributesMask & ~FileAttributeReadOnly",
        "FileCrossVolumeMovePreservationPolicy.RequiresNoSourceNamedDataStreamsAtProof",
        "FileCrossVolumeMovePreservationPolicy.RequiresNoSourceExtendedAttributesAtProof",
        ".ProbeAsync(request, cancellationToken)",
        "Later fidelity checks remain mandatory",
    )
    checks += reject(
        core,
        "SourceDeleteMutationAuthorized => true",
        "FileCrossVolumeMoveSourceDeleteAuthorization",
        "MarkCopyMutationStartedAsync",
        "CopyNewFileAsync",
        "MarkDeletePendingAsync",
    )

    fidelity = source["fidelity"]
    for shared in (
        "FileBasicMetadataEvidence.StableCopiedAttributesMask & ~FileAttributeReadOnly",
        "FileCrossVolumeMovePreservationPolicy.RequiresNoSourceNamedDataStreamsAtProof",
        "FileCrossVolumeMovePreservationPolicy.RequiresNoSourceExtendedAttributesAtProof",
    ):
        assert shared in fidelity and shared in core, shared
        checks += 1

    windows = source["windows"]
    checks += require(
        windows,
        "WindowsFileCrossVolumeMoveSourcePreflightProbe",
        "FileShare.Read | FileShare.Write | FileShare.Delete",
        "WindowsFileNamedDataStreamTopologyDigest.Read(source)",
        "NtQueryInformationFile(",
        "FileEaInformation",
        "GetFileInformationByHandle(",
        "GetFinalPathNameByHandleW(",
        "identity != request.SourceIdentity",
        "FileCrossVolumeMoveSourcePreflightClassifier.Classify(",
        "not\n/// misrepresented as a stability lease".replace("\\n", "\n"),
    )
    checks += reject(
        windows,
        "NtSetInformationFile(",
        "FileDispositionInformation",
        "File.Delete(",
        "File.Move(",
        "File.Copy(",
        "AccessSystemSecurity",
        "BackupSecurityInformation",
        "GetSecurityInfo(",
        "FileShare.Read,\n",
    )

    tests = source["tests"]
    checks += require(
        tests,
        "ReadOnlyNamedStreamAndEaAreDeterministicPreCopyBlockers",
        "OrdinarySupportedSourceMayContinueButPreflightNeverAuthorizesMutation",
        "UnsupportedSourcePreflightBlocksBeforeCompositeHistoryOrCopy",
        'Assert.AreEqual("CrossVolumeMoveValidationBlocked", result.Failure?.Code)',
        "Assert.AreEqual(0, copy.CallCount)",
        "Assert.AreEqual(0, sourceDelete.CallCount)",
        "Assert.IsNull(await history.GetAsync(plan.Id))",
        "SourcePreflightAuthorizesMutation",
    )
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--cases", type=int, default=50000)
    args = parser.parse_args()
    if args.cases < 1:
        raise ValueError("--cases must be positive")
    checks = check_repository(args.repo_root.resolve())
    checks += check_model(args.cases)
    print(f"PASS cross-volume Move source preflight verification ({checks} checks)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
