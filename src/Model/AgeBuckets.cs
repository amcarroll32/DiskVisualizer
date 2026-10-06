namespace DiskVisualizer.Model;

/// <summary>Ordinal last-modified buckets used by the "Color: Age" mode.</summary>
public static class AgeBuckets
{
    public const int Count = 6;
    public const int Unknown = -1;

    private static readonly long[] Limits =
    [
        TimeSpan.FromDays(7).Ticks,
        TimeSpan.FromDays(30).Ticks,
        TimeSpan.FromDays(182).Ticks,
        TimeSpan.FromDays(365).Ticks,
        TimeSpan.FromDays(3 * 365).Ticks,
    ];

    public static int Of(long utcTicks, long nowUtcTicks)
    {
        if (utcTicks <= 0)
            return Unknown;
        long age = nowUtcTicks - utcTicks;
        for (int i = 0; i < Limits.Length; i++)
            if (age < Limits[i])
                return i;
        return Limits.Length;
    }

    public static string Label(int bucket) => bucket switch
    {
        0 => "Under a week",
        1 => "1 week – 1 month",
        2 => "1 – 6 months",
        3 => "6 – 12 months",
        4 => "1 – 3 years",
        5 => "Over 3 years",
        _ => "Unknown date",
    };
}
