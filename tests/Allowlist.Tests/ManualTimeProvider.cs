namespace Allowlist.Tests;

public sealed class ManualTimeProvider : TimeProvider
{
    private long ticks = TimeProvider.System.GetUtcNow().Ticks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    public void Advance(TimeSpan amount) => Interlocked.Add(ref ticks, amount.Ticks);
}
