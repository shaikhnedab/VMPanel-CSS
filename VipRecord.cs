namespace VMPanel;

public sealed record VipRecord(
    ulong SteamId,
    string Flag,
    string Name,
    long ExpireStamp,
    DateTime CreatedAt,
    int Type
)
{
    public bool IsExpired(long now) => ExpireStamp <= now;

    public int DaysLeft(long now)
    {
        var seconds = ExpireStamp - now;
        return Math.Max(0, (int)Math.Floor(seconds / 86400.0));
    }
}