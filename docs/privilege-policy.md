# Privilege policy

FileOp's beta/initial production privilege policy is **same-account split-token elevation only**.

The desktop app remains unelevated. When native indexing requires administrative access, `IndexingServiceProcessSession` may request UAC elevation only when `WindowsProcessElevation.CanElevateCurrentIdentityInPlace()` proves the current account has an administrator split token. Credential-over-the-shoulder elevation to a different Windows account is rejected because the helper IPC and filesystem trust model are bound to the current user identity.

The elevated helper is additionally restricted to the exact adjacent `FileOp.Indexer.exe`, a build-pinned Authenticode signer, and a launch path the unelevated token cannot mutate.

A Windows service / ACL design for different-account administration is **not part of this release**. If it becomes a real requirement it must be reviewed as a new trust boundary: service identity, installation ACLs, client authentication, request authorization, impersonation, update trust and recovery all need explicit design. It must not be approximated by loosening the current-user pipe or accepting arbitrary `runas` credentials.
