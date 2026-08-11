# Known-location review handoff to Files

Known-location Optimize evidence can be handed to the existing indexed Files browser for inspection without creating a cleanup action.

Before navigation, FileOp revalidates that the cached review belongs to the currently active native indexed root and that the exact requested path is still a member of that review. The candidate and its parent must remain inside that root. The handoff is refused while Disk I/O attribution, duplicate content verification, or another Storage analysis owns the shared Storage gate.

A successful handoff opens the candidate's parent directory in the left Files pane through the existing exact paged browse path. If a left-pane tab is already on that same parent directory, FileOp reuses it; otherwise it creates one tab for the parent. This avoids accumulating duplicate tabs when the same review location is inspected repeatedly.

After the asynchronous indexed parent load returns, FileOp rechecks the cached review object, active root, native source mode, busy state and Optimize availability. If the source changed during the load, the parent can remain open but FileOp does not auto-select the stale review candidate.

When the source is still current, FileOp selects the candidate if it is already loaded. If it is beyond the first page, a path-bound selection hint is retained so an explicit **Load more** can reveal and select it; the handoff does not auto-page through the directory.

This is navigation and selection only. It does not prepare or queue a Copy/Move plan, run Files preflight/execution validation, authorize cleanup, or execute a filesystem mutation. It does not rescan the filesystem, hash content, add a protocol operation, or delete, move, copy or replace a candidate. Protocol remains v8.

The review button routes the candidate path to the app-owned `MainWindow` only to enter the existing Files navigation coordinator. That routing does not expose a mutation API; the destination remains the same indexed Files surface and its existing paging/selection state.
