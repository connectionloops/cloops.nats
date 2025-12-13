namespace CLOOPS.NATS.Locking;

/// <summary>
/// The lock handle object
/// </summary>
public sealed class DistributedLockHandle : IAsyncDisposable
{
    private readonly KvDistributedLock _outer;
    private readonly string _key;
    private readonly string _ownerId;
    private readonly KvDistributedLockOptions _opt;
    private readonly CancellationTokenSource linkedCt;
    private ulong _rev;

    internal DistributedLockHandle(KvDistributedLock outer, string key, ulong rev, string ownerId, KvDistributedLockOptions opt, CancellationToken ct)
    {
        _outer = outer;
        _key = key;
        _rev = rev;
        _ownerId = ownerId;
        _opt = opt;
        linkedCt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        StartRenewLoop();
    }

    internal void StartRenewLoop()
    {
        _ = RenewLoopAsync(linkedCt.Token);
    }

    private async Task RenewLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_opt.RenewInterval, ct);
                var ok = await _outer.RenewAsync(_key, _rev, _ownerId, ct);
                if (!ok) return; // lost lock; stop renewing
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Releases the held up lock 
    /// </summary>
    /// <returns></returns>
    public async ValueTask DisposeAsync()
    {
        try { linkedCt.Cancel(); } catch { }
        linkedCt.Dispose();

        // Best-effort release
        using var releaseCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _outer.ReleaseAsync(_key, _rev, _ownerId, releaseCts.Token);
    }
}