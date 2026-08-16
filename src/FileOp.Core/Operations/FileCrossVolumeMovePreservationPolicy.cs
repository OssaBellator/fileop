namespace FileOp.Core.Operations;

/// <summary>
/// Product-level preservation contract for regular-file Move across filesystem volumes.
/// A cross-volume Move necessarily creates a new destination filesystem object and removes
/// only the selected source directory entry; it cannot carry same-volume hard-link topology
/// to the new volume or make the copy/delete sequence globally atomic with unrelated metadata
/// writers.
///
/// FileOp therefore separates three questions:
/// 1. main-stream bytes must remain stable after the source-delete lease is acquired;
/// 2. unsupported source semantics that the current Copy primitive does not carry (named
///    streams and EAs) must be absent when destructive eligibility is proved;
/// 3. security and hard-link topology follow explicit Windows/path-entry semantics rather
///    than being falsely advertised as preserved across volumes.
/// </summary>
public static class FileCrossVolumeMovePreservationPolicy
{
    /// <summary>
    /// The selected source directory entry is the object being moved. Other hard links to
    /// the same source file remain valid source-volume directory entries and are not copied
    /// to the destination volume.
    /// </summary>
    public const bool MovesSelectedSourceDirectoryEntryOnly = true;

    /// <summary>
    /// Same-volume hard-link topology cannot be preserved by creating a new file on another
    /// volume. Link count is evidence/diagnostics, not destructive eligibility.
    /// </summary>
    public const bool PreservesHardLinkTopology = false;

    /// <summary>
    /// Main-stream content is a destructive invariant. The Windows source-delete capability
    /// must establish a sharing/stability boundary before the final content proof and retain
    /// it through same-handle deletion.
    /// </summary>
    public const bool RequiresStableMainStreamThroughDelete = true;

    /// <summary>
    /// The current Copy primitive does not preserve named streams or extended attributes.
    /// They must therefore be absent from the source whenever destructive eligibility is
    /// positively classified.
    /// </summary>
    public const bool RequiresNoSourceNamedDataStreamsAtProof = true;
    public const bool RequiresNoSourceExtendedAttributesAtProof = true;

    /// <summary>
    /// Cross-volume copy+delete is not an atomic transaction with unrelated metadata writers.
    /// Stable basic metadata is copied and compared at the proof checkpoints, but FileOp does
    /// not claim that Windows freezes FILE_WRITE_ATTRIBUTES/FILE_WRITE_EA or independent named
    /// streams between the last observation and source unlink. Such concurrent mutations are
    /// outside the atomicity guarantee rather than being silently described as kernel-frozen.
    /// </summary>
    public const bool GuaranteesAtomicConcurrentMetadataMutationCapture = false;

    public const string Summary =
        "Cross-volume Move preserves the selected file's validated main-stream snapshot and removes only the selected source directory entry. Source named streams/EAs must be absent at destructive proof; security uses destination-default semantics; hard-link topology is not recreated across volumes; unrelated concurrent metadata changes are not claimed to be atomically captured.";
}
