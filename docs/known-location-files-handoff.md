# Known-location review handoff to Files

Known-location Optimize evidence can be handed to the existing indexed Files browser for inspection without creating a cleanup action.

Before navigation, FileOp revalidates that the cached review belongs to the currently active native indexed root and that the exact requested path is still a member of that review. The candidate and its parent must remain inside that root. The handoff is refused while Disk I/O attribution, duplicate content verification, or another Storage analysis is active.

A successful handoff opens the candidate's parent directory in a new left-pane Files tab through the existing exact paged browse path. FileOp selects the candidate if it is already loaded. If it is beyond the first page, a path-bound selection hint is retained so an explicit Load more can reveal and select it; the handoff does not auto-page through the directory.

This is navigation and selection only; it does not authorize or run cleanup. It does not rescan the filesystem, hash content, add a protocol operation, or delete, move, copy, replace, or queue a candidate. Protocol remains v8.
