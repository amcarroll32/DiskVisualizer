namespace DiskVisualizer.Model;

/// <summary>
/// Ordinal last-modified buckets used by the "Color: Age" mode. Five, because that's the most
/// steps a single-hue ramp can hold while staying distinguishable on a light surface.
/// </summary>
public static class AgeBuckets
{
    public const int Count = 5;
    public const int Unknown = -1;

    private static readonly long[] Limits =
    [
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
        0 => "Under a month",
        1 => "1 – 6 months",
        2 => "6 – 12 months",
        3 => "1 – 3 years",
        4 => "Over 3 years",
        _ => "Unknown date",
    };
}
