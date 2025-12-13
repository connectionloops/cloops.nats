namespace CLOOPS.NATS.Locking;
internal class KvDistributedLockOptions
{
    public string BucketName { get; init; } = "locks";
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(20); // soft lease
    public TimeSpan RenewInterval { get; init; } = TimeSpan.FromSeconds(10); // heartbeat
    public TimeSpan AcquireRetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan AcquireRetryMaxDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public string OwnerId { get; init; } = $"{Environment.MachineName}:{Environment.ProcessId}";
}