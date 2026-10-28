namespace PrivatePaste.Desktop.Platform;

/// <summary>
/// Limits how often one clipboard change is retried after a read gave up because another application held the clipboard:
/// at most <see cref="MaxPerChange"/> re-arms per change (sequence number), so a clipboard that stays locked cannot keep
/// the watcher firing forever. Not thread-safe; the caller serializes.
/// </summary>
public sealed class ClipboardRearm
{
    public const int MaxPerChange = 3;

    private uint? _sequence;
    private int _count;

    /// <summary>True when the change with this sequence number may be raised again.</summary>
    public bool TryRearm(uint sequence)
    {
        if (_sequence != sequence)
        {
            _sequence = sequence;
            _count = 0;
        }
        if (_count >= MaxPerChange) return false;
        _count++;
        return true;
    }
}
