using System.Globalization;
using System.Text.Json;
using ClaudeTradingAgent.RiskManagement;
using ClaudeTradingAgent.Strategy;

namespace ClaudeTradingAgent.TradingAgent.Configuration;

/// <summary>
/// Shape of config/trading.json. The top-level keys are the v3 day-trading
/// spec verbatim; the nested sections carry what that spec leaves to the
/// implementation (strategy parameters, exit levels, order-rate limits, PDT).
/// </summary>
public sealed record TradingConfigFile(
    string Environment,
    bool TradingEnabled,
    decimal StrategyCapital,
    bool RegularHoursOnly,
    bool AllowExtendedHours,
    string? EntryStartTimeEt,
    string? EntryCutoffTimeEt,
    string? FlattenTimeEt,
    decimal MaxNotionalPerTrade,
    int MaxConcurrentPositions,
    decimal MaxTotalExposure,
    decimal MaxDailyLoss,
    decimal MaxEstimatedLossPerTrade,
    bool AllowMargin,
    bool AllowShortSelling,
    bool AllowOptions,
    bool AllowCrypto,
    bool AllowOvernightPositions,
    int MaxQuoteAgeSeconds,
    decimal MaxSpreadPercent,
    StrategyConfig? Strategy,
    ExitConfig? Exits,
    OrderLimitsConfig? OrderLimits,
    PatternDayTraderConfig? PatternDayTrader);

public sealed record StrategyConfig(string Name, decimal MinimumConfidence, int LookbackBars, decimal MinimumVolumeRatio);
public sealed record ExitConfig(decimal StopLossPercent, decimal TakeProfitPercent, int MaxHoldMinutes);
public sealed record OrderLimitsConfig(int MaxOrdersPerSymbolPerDay, int MaxTotalOrdersPerDay);
public sealed record PatternDayTraderConfig(decimal EquityThreshold, int MaxDayTradesUnderThreshold);

/// <summary>Shape of config/symbols.json. The denylist is optional; v3 omits it.</summary>
public sealed record SymbolConfigFile(IReadOnlyList<string>? Allowlist, IReadOnlyList<string>? Denylist, string? Notes);

/// <summary>
/// Loads both config files into the domain's policy types, once, at startup.
/// Every problem is collected and reported together, so one failed start
/// shows everything that is wrong rather than one error per redeploy.
/// </summary>
public sealed class TradingPolicySet
{
    public required MomentumPolicy Strategy { get; init; }
    public required RiskPolicy Risk { get; init; }
    public required SessionPolicy Session { get; init; }
    public required ExitPolicy Exits { get; init; }
    public required IReadOnlySet<string> Allowlist { get; init; }
    public required string StrategyName { get; init; }
    public required int LookbackBars { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static TradingPolicySet Load(AgentOptions options)
    {
        var t = Read<TradingConfigFile>(options.TradingConfigPath);
        var symbols = Read<SymbolConfigFile>(options.SymbolConfigPath);
        var errors = new List<string>();

        if (!string.Equals(t.Environment, "paper", StringComparison.OrdinalIgnoreCase))
            errors.Add($"environment is '{t.Environment}'; only 'paper' is supported.");

        // These features are not implemented. A flag set to true is refused
        // rather than ignored: silently running without a capability someone
        // switched on is worse than not starting.
        if (!t.RegularHoursOnly) errors.Add("regularHoursOnly must be true; this build trades the regular session only.");
        foreach (var (name, value) in new[]
                 {
                     ("allowExtendedHours", t.AllowExtendedHours), ("allowMargin", t.AllowMargin),
                     ("allowShortSelling", t.AllowShortSelling), ("allowOptions", t.AllowOptions),
                     ("allowCrypto", t.AllowCrypto), ("allowOvernightPositions", t.AllowOvernightPositions),
                 })
        {
            if (value) errors.Add($"{name} must be false; this build does not support it.");
        }

        if (t.StrategyCapital <= 0) errors.Add("strategyCapital must be greater than zero.");
        if (t.MaxNotionalPerTrade <= 0) errors.Add("maxNotionalPerTrade must be greater than zero.");
        if (t.MaxTotalExposure < t.MaxNotionalPerTrade) errors.Add("maxTotalExposure must be at least maxNotionalPerTrade.");
        if (t.MaxTotalExposure > t.StrategyCapital) errors.Add("maxTotalExposure cannot exceed strategyCapital.");
        if (t.MaxConcurrentPositions < 1) errors.Add("maxConcurrentPositions must be at least 1.");
        if (t.MaxDailyLoss <= 0) errors.Add("maxDailyLoss must be greater than zero.");
        if (t.MaxEstimatedLossPerTrade <= 0) errors.Add("maxEstimatedLossPerTrade must be greater than zero.");
        if (t.MaxQuoteAgeSeconds <= 0) errors.Add("maxQuoteAgeSeconds must be greater than zero.");
        if (t.MaxSpreadPercent is <= 0 or > 5) errors.Add("maxSpreadPercent must be greater than 0 and at most 5.");

        var entryStart = ParseTime(t.EntryStartTimeEt, "entryStartTimeEt", errors);
        var cutoff = ParseTime(t.EntryCutoffTimeEt, "entryCutoffTimeEt", errors);
        var flatten = ParseTime(t.FlattenTimeEt, "flattenTimeEt", errors);
        var session = new SessionPolicy(entryStart, cutoff, flatten);
        if (t.EntryStartTimeEt is not null && t.EntryCutoffTimeEt is not null && t.FlattenTimeEt is not null)
            errors.AddRange(session.Validate());

        if (t.Strategy is null) errors.Add("the 'strategy' section is missing.");
        if (t.Exits is null) errors.Add("the 'exits' section is missing.");
        if (t.OrderLimits is null) errors.Add("the 'orderLimits' section is missing.");

        var exits = t.Exits is null ? null : new ExitPolicy(t.Exits.StopLossPercent, t.Exits.TakeProfitPercent, TimeSpan.FromMinutes(t.Exits.MaxHoldMinutes));
        if (exits is not null)
        {
            errors.AddRange(exits.Validate());
            // A full-size trade whose stop already breaches the per-trade loss
            // limit would be rejected every time: a contradiction, not a policy.
            if (t.MaxNotionalPerTrade * exits.StopLossPercent / 100m > t.MaxEstimatedLossPerTrade)
                errors.Add("maxNotionalPerTrade × exits.stopLossPercent exceeds maxEstimatedLossPerTrade; every full-size trade would be rejected.");
        }

        var allowed = (symbols.Allowlist ?? [])
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => s.Length > 0)
            .Except((symbols.Denylist ?? []).Select(s => s.Trim().ToUpperInvariant()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowed.Count == 0) errors.Add("the symbol allowlist is empty; there is nothing the agent may trade.");

        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Invalid trading configuration ({options.TradingConfigPath}, {options.SymbolConfigPath}):\n  - "
                + string.Join("\n  - ", errors));

        var maxDataAge = TimeSpan.FromSeconds(t.MaxQuoteAgeSeconds);

        return new TradingPolicySet
        {
            StrategyName = t.Strategy!.Name,
            LookbackBars = t.Strategy.LookbackBars,
            Allowlist = allowed,
            Session = session,
            Exits = exits!,
            Strategy = new MomentumPolicy(
                t.Strategy.MinimumConfidence,
                t.Strategy.MinimumVolumeRatio,
                MaximumSpreadBps: t.MaxSpreadPercent * 100m,
                t.MaxNotionalPerTrade,
                maxDataAge),
            Risk = new RiskPolicy
            {
                // Both the file and the environment must agree before trading
                // is possible. Either one set to false is a kill switch.
                TradingEnabled = t.TradingEnabled && options.TradingEnabled,
                RequirePaperMode = true,
                StrategyCapital = t.StrategyCapital,
                MaxNotionalPerTrade = t.MaxNotionalPerTrade,
                MaxConcurrentPositions = t.MaxConcurrentPositions,
                MaxTotalExposure = t.MaxTotalExposure,
                MaxDailyLoss = t.MaxDailyLoss,
                MaxEstimatedLossPerTrade = t.MaxEstimatedLossPerTrade,
                StopLossPercent = exits!.StopLossPercent,
                MaxDataAge = maxDataAge,
                MaxOrdersPerSymbolPerDay = t.OrderLimits!.MaxOrdersPerSymbolPerDay,
                MaxTotalOrdersPerDay = t.OrderLimits.MaxTotalOrdersPerDay,
                PdtEquityThreshold = t.PatternDayTrader?.EquityThreshold ?? 0m,
                MaxDayTradesUnderPdt = t.PatternDayTrader?.MaxDayTradesUnderThreshold ?? 0,
            },
        };
    }

    private static TimeOnly ParseTime(string? value, string name, List<string> errors)
    {
        if (TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return time;
        errors.Add($"{name} must be a New York time in HH:mm form; got '{value}'.");
        return default;
    }

    private static T Read<T>(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required configuration file not found: {path}");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
               ?? throw new InvalidOperationException($"Configuration file {path} deserialised to null.");
    }
}
