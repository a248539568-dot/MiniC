using MiniC.Widgets;
using Xunit;

namespace MiniC.Tests;

public sealed class LatestRequestTests
{
    [Fact]
    public void ReplacedRequestCannotClearOrDisposeCurrentRequest()
    {
        var requests = new LatestRequest();
        var first = requests.Begin()!;
        var second = requests.Begin()!;

        Assert.True(first.IsCancellationRequested);
        requests.Complete(first);
        Assert.False(second.IsCancellationRequested);

        requests.Cancel();
        Assert.True(second.IsCancellationRequested);
        requests.Complete(second);
    }

    [Fact]
    public void StopCancelsCurrentAndPreventsFurtherRequests()
    {
        var requests = new LatestRequest();
        var pending = requests.Begin()!;

        requests.Stop();

        Assert.True(pending.IsCancellationRequested);
        Assert.Null(requests.Begin());
        requests.Complete(pending);
    }
}
