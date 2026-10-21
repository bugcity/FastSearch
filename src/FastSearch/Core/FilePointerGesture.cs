using System;

namespace FastSearch.Core;

/// <summary>Distinguishes a plain click from selection gestures and a drag, independently of WPF.</summary>
public sealed class FilePointerGesture
{
    private ulong? _pressedId;
    private double _x, _y;
    private bool _allowOpen, _dragged;
    public void Begin(ulong id, double x, double y, bool allowOpen)
    {
        _pressedId = id;
        _x = x;
        _y = y;
        _allowOpen = allowOpen;
        _dragged = false;
    }
    public bool Move(double x, double y, double thresholdX, double thresholdY)
    {
        if (_pressedId is null || _dragged || (Math.Abs(x - _x) < thresholdX && Math.Abs(y - _y) < thresholdY)) return false;
        _dragged = true;
        return true;
    }
    public bool End(ulong? releasedId)
    {
        bool open = _pressedId is not null && _pressedId == releasedId && _allowOpen && !_dragged;
        Cancel();
        return open;
    }
    public void Cancel() { _pressedId = null; _allowOpen = false; _dragged = false; }
}
