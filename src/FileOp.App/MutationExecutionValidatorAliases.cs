// Production Files mutation composition is deliberately centralized here while the current
// mutation FileIdentity remains the 64-bit BY_HANDLE_FILE_INFORMATION index. The validator
// aliases provide the normal early refusal path; the primitive/provider aliases repeat the
// same exact-bound NTFS requirement immediately before raw mutation capability acquisition.
//
// These aliases affect only FileOp.App compilation. The underlying Windows validators and raw
// mutation providers remain directly testable without the product policy wrappers. Browsing,
// Search, indexing and read-only preflight are intentionally not narrowed to NTFS.
global using WindowsFileOperationExecutionValidator =
    FileOp.Windows.Operations.WindowsNtfsMutationExecutionValidator;
global using WindowsMoveOperationExecutionValidator =
    FileOp.Windows.Operations.WindowsNtfsMoveOperationExecutionValidator;
global using WindowsFileDeleteOperationExecutionValidator =
    FileOp.Windows.Operations.WindowsNtfsFileDeleteOperationExecutionValidator;

global using WindowsFileCopyMutationPrimitive =
    FileOp.Windows.Operations.WindowsNtfsFileCopyMutationPrimitive;
global using WindowsFileSameVolumeMoveMutationPrimitive =
    FileOp.Windows.Operations.WindowsNtfsFileSameVolumeMoveMutationPrimitive;
global using WindowsFileDeleteOperationFinalMutationLeaseProvider =
    FileOp.Windows.Operations.WindowsNtfsFileDeleteOperationFinalMutationLeaseProvider;
