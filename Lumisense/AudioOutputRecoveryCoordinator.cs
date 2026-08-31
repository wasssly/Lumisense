using System;

namespace Lumisense;

/// <summary>
/// Guards controlled output recovery against re-entry and repeated recovery during cooldown.
/// The component does not touch WASAPI or decide how a track is resumed.
/// </summary>
internal sealed class AudioOutputRecoveryCoordinator
{
    private bool _isInProgress;
    private DateTime _lastStartedUtc = DateTime.MinValue;

    public bool IsInProgress => _isInProgress;
    public DateTime LastStartedUtc => _lastStartedUtc;
    public int RecoveryCount { get; private set; }

    public bool TryBegin(DateTime nowUtc, TimeSpan cooldown)
    {
        if (_isInProgress)
            return false;

        if (_lastStartedUtc != DateTime.MinValue && nowUtc - _lastStartedUtc < cooldown)
            return false;

        _isInProgress = true;
        _lastStartedUtc = nowUtc;
        RecoveryCount++;
        return true;
    }

    public void Complete()
    {
        _isInProgress = false;
    }
}
