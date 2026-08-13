namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    private long _fallbackStorageSourceGeneration;
    private int _fallbackStorageSourceActive;

    public DesktopSearchEngine()
    {
        // This handler is registered before any MainWindow subscriber. It turns a
        // successfully published fallback snapshot into a stable source generation
        // without coupling the cache boundary to BuildFallbackAsync internals.
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
                    $"native:{volume.VolumeIdentity:X16}",
                DesktopSearchMode.Fallback when _fallbackReady =>
                    $"fallback:{Volatile.Read(ref _fallbackStorageSourceGeneration)}",
                _ => null,
            };
        }
    }

    private void TrackStorageSourceIdentity(DesktopSearchEngineState state)
    {
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
