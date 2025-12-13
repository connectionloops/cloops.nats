using System.Text.Json;
using System.Diagnostics;
using NATS.Client.KeyValueStore;

namespace CLOOPS.NATS.Locking;

readonly record struct LockDoc(string owner, long expiresAtUnixMs);

/// <summary>
/// Represents the locker to enable distributed locking functionality
/// </summary>
internal sealed class KvDistributedLock : IAsyncDisposable
{
    private readonly KvDistributedLockOptions _opt;

    // Replace these with actual NATS JetStream/KV interfaces from your SDK version.
    private INatsKVStore _kv = default!;

    /// <summary>
    /// Initializes the locker
    /// </summary>
    /// <param name="kv">INatsStore kv</param>
    /// <param name="options">KvDistributedLockOptions options</param>
    internal KvDistributedLock(INatsKVStore kv, KvDistributedLockOptions? options = null)
    {
        _opt = options ?? new KvDistributedLockOptions();
        _kv = kv;
    }

    internal async Task<DistributedLockHandle?> TryAcquireAsync(string resourceId, TimeSpan? _timeout = null, string? _ownerId = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var key = resourceId;
        var timeout = _timeout ?? _opt.AcquireRetryMaxDelay * 1.5;
        var ownerId = _ownerId ?? _opt.OwnerId;

        while (!ct.IsCancellationRequested && sw.Elapsed < timeout)
        {
            // 1) Read current
            var res = await _kv.TryGetEntryAsync<LockDoc>(key, cancellationToken: ct); // returns (found:boolean, entry)
            if (!res.Success)
            {
                // 2a) Create brand new (only if absent)
                var doc = new LockDoc(ownerId, DateTimeOffset.UtcNow.Add(_opt.LeaseDuration).ToUnixTimeMilliseconds());
                var createdRev = await _kv.CreateAsync(key, doc, cancellationToken: ct); // throws if it can't create
                var handle = new DistributedLockHandle(this, key, createdRev, ownerId, _opt, ct);
                return handle;
            }
            else
            {
                var doc = res.Value.Value;
                var rev = res.Value.Revision;
                // 2b) Exists: check expiry
                var isExpired = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= doc.expiresAtUnixMs;

                if (isExpired)
                {
                    // 3) Steal lock by compare and set CAS update
                    var newDoc = new LockDoc(ownerId, DateTimeOffset.UtcNow.Add(_opt.LeaseDuration).ToUnixTimeMilliseconds());
                    var newRevision = await _kv.TryUpdateAsync(key, newDoc, rev, cancellationToken: ct);
                    if (newRevision.Success)
                    {
                        var handle = new DistributedLockHandle(this, key, newRevision.Value, ownerId, _opt, ct);
                        return handle;
                    }
                }
            }

            // Backoff with jitter
            var delay = Jittered(_opt.AcquireRetryBaseDelay, _opt.AcquireRetryMaxDelay);
            await Task.Delay(delay, ct);
        }

        return null;
    }

    /// <summary>
    /// Renews the lock
    /// You have have the latest rev
    /// </summary>
    /// <param name="key">lock key</param>
    /// <param name="currentRev">your current rev</param>
    /// <param name="ownerId">owner id</param>
    /// <param name="ct">cancellation token</param>
    /// <returns></returns>
    internal async Task<bool> RenewAsync(string key, ulong currentRev, string ownerId, CancellationToken ct)
    {
        // Extend lease if still ours
        var res = await _kv.TryGetEntryAsync<LockDoc>(key, currentRev, cancellationToken: ct);
        if (!res.Success) return false;

        var doc = res.Value.Value;
        var rev = res.Value.Revision;
        if (rev != currentRev) return false; // someone changed it

        if (!StringComparer.Ordinal.Equals(doc.owner, ownerId)) return false; // owned by someone else

        var updated = doc with { expiresAtUnixMs = DateTimeOffset.UtcNow.Add(_opt.LeaseDuration).ToUnixTimeMilliseconds() };

        return (await _kv.TryUpdateAsync(key, updated, rev, cancellationToken: ct)).Success;
    }

    /// <summary>
    /// Releases the currently holding lock
    /// You have to have latest revision
    /// </summary>
    /// <param name="key">Lock key</param>
    /// <param name="currentRev">your current revision</param>
    /// <param name="ownerId">owner id</param>
    /// <param name="ct"></param>
    /// <returns></returns>
    internal async Task<bool> ReleaseAsync(string key, ulong currentRev, string ownerId, CancellationToken ct)
    {
        // Best-effort delete with CAS so we don’t erase someone else’s lock
        var res = await _kv.TryGetEntryAsync<LockDoc>(key, currentRev, cancellationToken: ct);
        if (!res.Success) return true; // already gone

        var doc = res.Value.Value;
        var rev = res.Value.Revision;

        if (rev != currentRev) return true; // moved on, treat as released
        if (!StringComparer.Ordinal.Equals(doc.owner, ownerId)) return true; // not ours
        return (await _kv.TryDeleteAsync(key, new NatsKVDeleteOpts { Revision = rev })).Success;
    }

    private static TimeSpan Jittered(TimeSpan min, TimeSpan max)
    {
        var rnd = Random.Shared.NextDouble();
        var ms = min.TotalMilliseconds + rnd * (max - min).TotalMilliseconds;
        return TimeSpan.FromMilliseconds(ms);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

}