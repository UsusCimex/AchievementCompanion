namespace AC.Core;

/// <summary>Русские числительные: 1 достижение, 2 достижения, 5 достижений, 21 достижение, 11 достижений.</summary>
public static class Ru
{
    public static string Plural(long n, string one, string few, string many)
    {
        n = Math.Abs(n);
        if (n % 100 is >= 11 and <= 14) return many;
        return (n % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }

    /// <summary>Число со словом: "32 достижения".</summary>
    public static string Count(long n, string one, string few, string many) => $"{n} {Plural(n, one, few, many)}";
}
