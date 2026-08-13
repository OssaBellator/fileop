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
            }

            if (state.IsBusy)
            {
                // Mark the current healthy source epoch inactive, but defer the
                // generation advance until native service availability recovers.
                // That makes every root-keyed feature observe its forced cache miss
                // when it is actually able to reload rather than while it is busy.
                Volatile.Write(ref _nativeStorageSourceActive, 0);
            }
            else
            {
                var wasActive = Interlocked.Exchange(ref _nativeStorageSourceActive, 1);
                if (sessionChanged || identityChanged || wasActive == 0)
                {
                    Interlocked.Increment(ref _nativeStorageSourceGeneration);
                }
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
