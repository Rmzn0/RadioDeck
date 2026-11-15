namespace RadioDeck.Services;

/// <summary>
/// Ensures network-level sanity: only one TX stream may be active at a time (configurable).
/// </summary>
public sealed class TxLock
{
    private readonly object _sync = new();
    private string? _owner;

    public bool TryAcquire(string ownerId)
    {
        lock (_sync)
        {
            if (_owner is null)
            {
                _owner = ownerId;
                return true;
            }
            return _owner == ownerId;
        }
    }

    public void Release(string ownerId)
    {
        lock (_sync)
        {
            if (_owner == ownerId) _owner = null;
        }
    }

    public string? Owner
    {
        get { lock (_sync) return _owner; }
    }
}
