using System.Windows;
using MiniC.Models;
using Point = System.Windows.Point;

namespace MiniC.Services;

/// <summary>Arm a merge only after a deliberate pause; an initial overlap is not a new drop intent.</summary>
internal sealed class BoxMergeGesture
{
    internal const int DwellMilliseconds = 200;
    private BoxTabRef? _initialTarget;
    private BoxTabRef? _candidate;
    private Point _anchor;
    private long _enteredAt;
    internal BoxTabRef? ReadyTarget { get; private set; }

    internal void Reset(BoxTabRef? initialTarget = null)
    {
        _initialTarget = initialTarget;
        _candidate = null;
        ReadyTarget = null;
    }

    internal BoxTabRef? Update(BoxTabRef? candidate, Point pointer, long milliseconds, double dpiScale = 1)
    {
        if (_initialTarget is not null)
        {
            if (candidate == _initialTarget) return null;
            _initialTarget = null;
        }
        var tolerance = 10 * (double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1);
        if (candidate != _candidate || (ReadyTarget is null && (pointer - _anchor).Length > tolerance))
        {
            _candidate = candidate;
            _anchor = pointer;
            _enteredAt = milliseconds;
            ReadyTarget = null;
        }
        if (candidate is not null && milliseconds - _enteredAt >= DwellMilliseconds) ReadyTarget = candidate;
        return ReadyTarget;
    }
}
