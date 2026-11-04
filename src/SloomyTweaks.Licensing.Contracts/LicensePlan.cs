namespace SloomyTweaks.Licensing;

/// <summary>License durations sold for Sloomy Tweaks.</summary>
public enum LicensePlan
{
    Days7 = 7,
    Days30 = 30,
    Days90 = 90,
    Days365 = 365,
    Lifetime = 0,
}

public static class LicensePlanExtensions
{
    /// <summary>Duration of the plan, or null for lifetime licenses.</summary>
    public static TimeSpan? Duration(this LicensePlan plan) => plan switch
    {
        LicensePlan.Lifetime => null,
        _ => TimeSpan.FromDays((int)plan),
    };

    public static string DisplayName(this LicensePlan plan) => plan switch
    {
        LicensePlan.Days7 => "7 days",
        LicensePlan.Days30 => "30 days",
        LicensePlan.Days90 => "90 days",
        LicensePlan.Days365 => "365 days",
        LicensePlan.Lifetime => "Lifetime",
        _ => plan.ToString(),
    };

    public static bool TryParse(string? value, out LicensePlan plan)
    {
        plan = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        switch (value.Trim().ToLowerInvariant())
        {
            case "7": case "days7": plan = LicensePlan.Days7; return true;
            case "30": case "days30": plan = LicensePlan.Days30; return true;
            case "90": case "days90": plan = LicensePlan.Days90; return true;
            case "365": case "days365": plan = LicensePlan.Days365; return true;
            case "lifetime": case "0": plan = LicensePlan.Lifetime; return true;
            default: return false;
        }
    }
}
