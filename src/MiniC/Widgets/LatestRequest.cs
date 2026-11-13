namespace MiniC.Widgets;

/// <summary>Owns the latest cancellation source without disposing an in-flight request.</summary>
internal sealed class LatestRequest
{
    private CancellationTokenSource? _current;
    private bool _stopped;

    public CancellationTokenSource? Begin()
    {
        if (_stopped) return null;
        _current?.Cancel();
        return _current = new CancellationTokenSource();
    }

    public void Complete(CancellationTokenSource request)
    {
        if (ReferenceEquals(_current, request)) _current = null;
        request.Dispose();
    }

    public void Cancel()
    {
        _current?.Cancel();
        _current = null;
    }

    public void Stop()
    {
        _stopped = true;
        Cancel();
    }
}
