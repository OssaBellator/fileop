namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    private long _nativeStorageSourceGeneration;
    private int _nativeStorageSourceActive;
    private int _nativeStorageSourceElevationBusy;
    private object? _trackedNativeStorageSession;
    private ulong _trackedNativeStorageVolumeIdentity;
    private int _trackedNativeStorageCanElevate;
    private long _fallbackStorageSourceGeneration;
    private int _fallbackStorageSourceActive;

    internal string? StorageSourceIdentityKey
    {
        get
        {
            var state = State;
            return state.Mode switch
            {
                DesktopSearchMode.Native when _primaryVolume is { } volume =>
                    $"native:{volume.VolumeIdentity:X16}:{Volatile.Read(ref _nativeStorageSourceGeneration)}",
                DesktopSearchMode.Fallback when _fallbackReady =>
                    $"fallback:{Volatile.Read(ref _fallbackStorageSourceGeneration)}",
                _ => null,
            };
        }
    }

    private void TrackStorageSourceIdentity(DesktopSearchEngineState state)
    {
        if (state.Mode == DesktopSearchMode.Native &&
            _nativeSession is { } nativeSession &&
            _primaryVolume is { } nativeVolume)
        {
            var previousCanElevate = Volatile.Read(ref _trackedNativeStorageCanElevate) == 1;
            var sessionChanged = !ReferenceEquals(_trackedNativeStorageSession, nativeSession);
            var identityChanged = _trackedNativeStorageVolumeIdentity != nativeVolume.VolumeIdentity;
            if (sessionChanged || identityChanged)
            {
                _trackedNativeStorageSession = nativeSession;
                _trackedNativeStorageVolumeIdentity = nativeVolume.VolumeIdentity;
            }

            if (state.IsBusy)
            {
                // Native elevation temporarily publishes IsBusy=true before it
                // knows whether the helper/session will actually change. Preserve
                // the current cache epoch across that user-requested busy interval;
                // a successful replacement is still detected by session/identity.
                if (Volatile.Read(ref _nativeStorageSourceElevationBusy) == 1 ||
                    (!sessionChanged && !identityChanged && previousCanElevate))
                {
                    Volatile.Write(ref _nativeStorageSourceElevationBusy, 1);
                }
                else
                {
                    // Background maintenance can replace the SQLite snapshot in
                    // place while keeping VolumeIdentity stable. Mark the healthy
                    // source epoch inactive and advance it when availability returns.
                    Volatile.Write(ref _nativeStorageSourceActive, 0);
                }
            }
            else
            {
                var elevationBusy =
                    Interlocked.Exchange(ref _nativeStorageSourceElevationBusy, 0) == 1;
                var wasActive = Interlocked.Exchange(ref _nativeStorageSourceActive, 1);
                if (sessionChanged || identityChanged || wasActive == 0)
                {
                    if (sessionChanged || identityChanged || !elevationBusy)
                    {
                        Interlocked.Increment(ref _nativeStorageSourceGeneration);
                    }
                }
            }

            Volatile.Write(ref _trackedNativeStorageCanElevate, state.CanElevate ? 1 : 0);
        }
        else if (state.Mode != DesktopSearchMode.Native)
        {
            Volatile.Write(ref _nativeStorageSourceActive, 0);
            Volatile.Write(ref _nativeStorageSourceElevationBusy, 0);
            _trackedNativeStorageSession = null;
            _trackedNativeStorageVolumeIdentity = 0;
            Volatile.Write(ref _trackedNativeStorageCanElevate, 0);
        }

        if (state.Mode == DesktopSearchMode.Fallback && _fallbackReady)
        {
            if (Interlocked.Exchange(ref _fallbackStorageSourceActive, 1) == 0)
            {
                Interlocked.Increment(ref _fallbackStorageSourceGeneration);
            }

            return;
        }

        if (state.Mode != DesktopSearchMode.Fallback)
        {
            Volatile.Write(ref _fallbackStorageSourceActive, 0);
        }
    }
}
