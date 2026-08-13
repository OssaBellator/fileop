param(
    [switch]$OfflineOnly,
    [switch]$SkipBenchmarks,
    [switch]$SkipWinUI
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Command
    )

    Write-Host "`n==> $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw "Python 3 is required for the offline FileOp verifiers."
}

Invoke-Step "Offline Storage UI/property verifier" {
    python tools/verify_storage_ui.py --repo-root $repoRoot
}

Invoke-Step "Offline Storage UI edge-case verifier" {
    python tools/verify_storage_ui_edgecases.py
}

Invoke-Step "Offline Storage source identity verifier" {
    python tools/verify_storage_source_identity.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline Storage file-type verifier" {
    python tools/verify_storage_types.py --repo-root $repoRoot
}

Invoke-Step "Offline Storage file-type randomized verifier" {
    python tools/verify_storage_types_fuzz.py --cases 1000
}

Invoke-Step "Offline Storage optimization verifier" {
    python tools/verify_storage_optimization.py --repo-root $repoRoot --cases 10000
}

Invoke-Step "Offline same-size content verification verifier" {
    python tools/verify_same_size_content_verification.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline physical reclaim evidence verifier" {
    python tools/verify_physical_reclaim_evidence.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline Storage threshold overlay verifier" {
    python tools/verify_storage_threshold_overlay.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline Storage threshold preference verifier" {
    python tools/verify_storage_threshold_preferences.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline known-location review verifier" {
    python tools/verify_known_location_review.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline known-location Files handoff verifier" {
    python tools/verify_known_location_files_handoff.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline cleanup readiness verifier" {
    python tools/verify_cleanup_readiness.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline cleanup physical release verifier" {
    python tools/verify_cleanup_physical_release.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline performance diagnostics verifier" {
    python tools/verify_performance_diagnostics.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline FileOp resource footprint verifier" {
    python tools/verify_fileop_resource_footprint.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline background process activity verifier" {
    python tools/verify_background_process_activity.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline Performance DiskIo UI verifier" {
    python tools/verify_performance_disk_io_ui.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline Performance DiskIo timing UI verifier" {
    python tools/verify_performance_disk_io_timing_ui.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline latency distribution verifier" {
    python tools/verify_latency_distributions.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline index database diagnostics verifier" {
    python tools/verify_index_diagnostics.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline USN/checkpoint freshness verifier" {
    python tools/verify_usn_freshness.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline disk I/O attribution verifier" {
    python tools/verify_disk_io_attribution.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline DiskIo response-timing verifier" {
    python tools/verify_disk_io_response_timing.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline DiskIo timing provenance verifier" {
    python tools/verify_disk_io_timing_provenance.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline DiskIo process response-timing verifier" {
    python tools/verify_disk_io_process_response_timing.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline DiskIo bottleneck evidence verifier" {
    python tools/verify_disk_io_bottleneck_evidence.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline disk I/O capture-policy verifier" {
    python tools/verify_disk_io_capture_policy.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline DiskIo ETW decoder verifier" {
    python tools/verify_disk_io_etw_decoder.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline DiskIo trace-metadata verifier" {
    python tools/verify_disk_io_trace_metadata.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline DiskIo system-session policy verifier" {
    python tools/verify_disk_io_system_session_policy.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline DiskIo trace-controller verifier" {
    python tools/verify_disk_io_trace_controller.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline DiskIo consumer-lifecycle verifier" {
    python tools/verify_disk_io_consumer_lifecycle.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline DiskIo native-consumer verifier" {
    python tools/verify_disk_io_native_consumer.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline ETW event-record snapshot verifier" {
    python tools/verify_etw_event_record_snapshot.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline DiskIo event-record bridge verifier" {
    python tools/verify_disk_io_event_record_bridge.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline DiskIo thread/process resolver verifier" {
    python tools/verify_disk_io_thread_process_resolver.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline DiskIo trace-evidence verifier" {
    python tools/verify_disk_io_trace_evidence.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline DiskIo trace-evidence lifecycle verifier" {
    python tools/verify_disk_io_trace_evidence_source.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline DiskIo loss-evidence verifier" {
    python tools/verify_disk_io_loss_evidence.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline DiskIo capture-collector verifier" {
    python tools/verify_disk_io_capture_collector.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline Windows DiskIo attribution-provider verifier" {
    python tools/verify_windows_disk_io_attribution_provider.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline Storage history verifier" {
    python tools/verify_storage_history.py --repo-root $repoRoot --cases 1000
}

Invoke-Step "Offline Storage history Unicode-root verifier" {
    python tools/verify_storage_history_unicode.py --repo-root $repoRoot
}

Invoke-Step "Offline Storage history service verifier" {
    python tools/verify_storage_history_service.py --repo-root $repoRoot --cases 2000
}

Invoke-Step "Offline Storage history UI/scheduler verifier" {
    python tools/verify_storage_history_ui.py --repo-root $repoRoot --cases 10000
}

Invoke-Step "Offline Storage pressure/history verifier" {
    python tools/verify_storage_pressure_history.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline indexed Files browser verifier" {
    python tools/verify_files_ui.py --repo-root $repoRoot --cases 10000
}

Invoke-Step "Offline file operation state verifier" {
    python tools/verify_file_operation_state.py --repo-root $repoRoot --cases 20000
}

Invoke-Step "Offline file operation preflight verifier" {
    python tools/verify_file_operation_preflight.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline file operation execution validation verifier" {
    python tools/verify_file_operation_execution_validation.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline file delete history-binding verifier" {
    python tools/verify_file_delete_history_binding.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline file delete pre-mutation preparation verifier" {
    python tools/verify_file_delete_pre_mutation_preparation.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline final file delete mutation-lease contract verifier" {
    python tools/verify_file_delete_final_mutation_lease_contract.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline Windows final delete-capability provider verifier" {
    python tools/verify_windows_file_delete_final_mutation_lease_provider.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline file delete mutation-barrier verifier" {
    python tools/verify_file_delete_mutation_barrier.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline same-handle file delete mutation verifier" {
    python tools/verify_file_delete_same_handle_mutation.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline multi-entry file delete orchestration verifier" {
    python tools/verify_file_delete_multi_entry_orchestration.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline file delete completion-only retry verifier" {
    python tools/verify_file_delete_completion_retry.py --repo-root $repoRoot
}

Invoke-Step "Offline Files delete session verifier" {
    python tools/verify_files_delete_session.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline file delete recovery-history discovery verifier" {
    python tools/verify_file_delete_recovery_history_discovery.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline file operation action-history verifier" {
    python tools/verify_file_operation_action_history.py --repo-root $repoRoot --cases 20000
}

Invoke-Step "Offline paged directory browse verifier" {
    python tools/verify_directory_browse.py --repo-root $repoRoot --cases 10000
}

if ($OfflineOnly) {
    Write-Host "`nPASS: offline FileOp verification completed without GitHub Actions or the .NET SDK." -ForegroundColor Green
    return
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK is required for the build/test portion. Install the .NET 10 SDK or rerun with -OfflineOnly."
}

Invoke-Step "FileOp.Core Release build" {
    dotnet build src/FileOp.Core/FileOp.Core.csproj --configuration Release
}

if (-not $SkipBenchmarks) {
    Invoke-Step "Benchmark harness Release build" {
        dotnet build benchmarks/FileOp.Benchmarks/FileOp.Benchmarks.csproj --configuration Release
    }
}

Invoke-Step "FileOp.Windows Release build" {
    dotnet build src/FileOp.Windows/FileOp.Windows.csproj --configuration Release
}

Invoke-Step "FileOp.Indexer x64 Release build" {
    dotnet build src/FileOp.Indexer/FileOp.Indexer.csproj --configuration Release -p:Platform=x64
}

$indexerPath = Join-Path $repoRoot "src/FileOp.Indexer/bin/x64/Release/net10.0-windows10.0.17763.0/FileOp.Indexer.exe"
if (-not (Test-Path -LiteralPath $indexerPath)) {
    throw "Indexer host was not produced at the expected path: $indexerPath"
}

$previousIndexerPath = $env:FILEOP_INDEXER_PATH
try {
    $env:FILEOP_INDEXER_PATH = $indexerPath
    Invoke-Step "Windows regression/integration tests" {
        dotnet test tests/FileOp.Windows.Tests/FileOp.Windows.Tests.csproj --configuration Release
    }
}
finally {
    $env:FILEOP_INDEXER_PATH = $previousIndexerPath
}

if (-not $SkipWinUI) {
    Invoke-Step "FileOp.App WinUI x64 Release build" {
        dotnet build src/FileOp.App/FileOp.App.csproj --configuration Release -p:Platform=x64
    }

    $appDirectory = Join-Path $repoRoot "src/FileOp.App/bin/x64/Release/net10.0-windows10.0.26100.0"
    $requiredArtifacts = @(
        "FileOp.Indexer.exe",
        "FileOp.Indexer.dll",
        "FileOp.Indexer.deps.json",
        "FileOp.Indexer.runtimeconfig.json"
    )

    Invoke-Step "Bundled indexer artifact check" {
        foreach ($name in $requiredArtifacts) {
            $path = Join-Path $appDirectory $name
            if (-not (Test-Path -LiteralPath $path)) {
                throw "Missing bundled indexing helper artifact: $path"
            }
        }
    }

    $previousIndexerPath = $env:FILEOP_INDEXER_PATH
    try {
        $env:FILEOP_INDEXER_PATH = Join-Path $appDirectory "FileOp.Indexer.exe"
        Invoke-Step "Bundled helper real-process handshake" {
            dotnet test tests/FileOp.Windows.Tests/FileOp.Windows.Tests.csproj `
                --configuration Release `
                --no-build `
                --filter IndexerProcessSessionCompletesRealHostHandshake
        }
    }
    finally {
        $env:FILEOP_INDEXER_PATH = $previousIndexerPath
    }
}

Write-Host "`nPASS: local FileOp verification completed without GitHub Actions." -ForegroundColor Green
