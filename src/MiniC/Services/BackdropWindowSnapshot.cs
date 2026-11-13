namespace MiniC.Services;

/// <summary>Window geometry changes independently of captured pixels.</summary>
internal sealed class BackdropWindowSnapshot(Func<BackdropWindow[]> capture)
{
    private BackdropWindow[] _windows = [];
    private long? _lastRefresh;

    internal BackdropWindow[] Get(long now, bool moving)
    {
        var interval = moving ? 16 : 100;
        if (_lastRefresh is null || now < _lastRefresh.Value || now - _lastRefresh.Value >= interval)
        {
            _windows = capture();
            _lastRefresh = now;
        }
        return _windows;
    }
}
