// Production Files mutation composition is deliberately centralized here while the current
// mutation FileIdentity remains the 64-bit BY_HANDLE_FILE_INFORMATION index. These aliases
// affect only FileOp.App compilation; the underlying Windows validators remain directly
// testable without the product mutation policy wrapper.
//
// Browsing, Search, indexing and read-only preflight are intentionally not narrowed to NTFS.
global using WindowsFileOperationExecutionValidator =
    FileOp.Windows.Operations.WindowsNtfsMutationExecutionValidator;
global using WindowsMoveOperationExecutionValidator =
    FileOp.Windows.Operations.WindowsNtfsMoveOperationExecutionValidator;
global using WindowsFileDeleteOperationExecutionValidator =
    FileOp.Windows.Operations.WindowsNtfsFileDeleteOperationExecutionValidator;
