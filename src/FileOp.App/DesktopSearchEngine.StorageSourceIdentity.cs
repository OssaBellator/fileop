namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    private long _nativeStorageSourceGeneration;
    private int _nativeStorageSourceActive;
    private object? _trackedNativeStorageSession;
    private ulong _trackedNativeStorageVolumeIdentity;
    private long _fallbackStorageSourceGeneration;
    private int _fallbackStorageSourceActive;

    public DesktopSearchEngine()
    {
        // This handler is registered before any MainWindow subscriber. It turns
        // usable native/fallback publications into stable source generations
        // without coupling MainWindow cache logic to lifecycle implementation.
        StateChanged += TrackStorageSourceIdentity;
    }

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
            var sessionChanged = !ReferenceEquals(_trackedNativeStorageSession, nativeSession);
            var identityChanged = _trackedNativeStorageVolumeIdentity != nativeVolume.VolumeIdentity;
            if (sessionChanged || identityChanged)
            {
                _trackedNativeStorageSession = nativeSession;
                _trackedNativeStorageVolumeIdentity = nativeVolume.VolumeIdentity;
                Interlocked.Increment(ref _nativeStorageSourceGeneration);
                Volatile.Write(ref _nativeStorageSourceActive, state.IsBusy ? 0 : 1);
            }
            else if (state.IsBusy)
            {
                // A busy native maintenance epoch can replace the SQLite snapshot
                // in place while preserving the physical VolumeIdentity. Advance
                // once when that epoch starts; repeated busy status updates do not
                // churn the cache identity, and recovery reuses this new generation.
                if (Interlocked.Exchange(ref _nativeStorageSourceActive, 0) == 1)
                {
                    Interlocked.Increment(ref _nativeStorageSourceGeneration);
                }
            }
            else
            {
                Volatile.Write(ref _nativeStorageSourceActive, 1);
            }
        }
        else if (state.Mode != DesktopSearchMode.Native)
        {
            Volatile.Write(ref _nativeStorageSourceActive, 0);
            _trackedNativeStorageSession = null;
            _trackedNativeStorageVolumeIdentity = 0;
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
