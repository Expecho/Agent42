namespace Agent42.Backend;

/// <summary>
/// How many push notifications the model sent during one fleet task (ADR 0154). The service creates it and the task's
/// <see cref="FleetPushTools"/> spends it, so the per-task cap and the count the service reads are one number. Agents of
/// one task run concurrently, hence the lock.
/// </summary>
public sealed class FleetPushCounter
{
    private readonly Lock _gate = new();
    private int _count;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Takes one send when fewer than <paramref name="max"/> are used; false at the cap.</summary>
    public bool TryTake(int max)
    {
        lock (_gate)
        {
            if (_count >= max)
            {
                return false;
            }

            _count++;
            return true;
        }
    }
}
