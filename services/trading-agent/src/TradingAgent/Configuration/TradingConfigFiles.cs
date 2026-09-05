using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeTradingAgent.RiskManagement;
using ClaudeTradingAgent.Strategy;

namespace ClaudeTradingAgent.TradingAgent.Configuration;

/// <summary>Shape of config/trading.json.</summary>
public sealed record TradingConfigFile(
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("tradingEnabled")] bool TradingEnabled,
    [property: JsonPropertyName("strategy")] StrategyConfig? Strategy,
    [property: JsonPropertyName("session")] SessionConfig? Session,
    [property: JsonPropertyName("exits")] ExitConfig? Exits,
    [property: JsonPropertyName("risk")] RiskConfig? Risk);

public sealed record StrategyConfig(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("minimumConfidence")] decimal MinimumConfidence,
    [property: JsonPropertyName("lookbackBars")] int LookbackBars,
    [property: JsonPropertyName("minimumVolumeRatio")] decimal MinimumVolumeRatio,
    [property: JsonPropertyName("maximumSpreadBps")] decimal MaximumSpreadBps);

/// <summary>Where inside the trading day the agent may act. Minutes, because that is how a trader thinks about a session.</summary>
public sealed record SessionConfig(
    [property: JsonPropertyName("skipFirstMinutesAfterOpen")] int SkipFirstMinutesAfterOpen,
    [property: JsonPropertyName("noNewEntriesMinutesBeforeClose")] int NoNewEntriesMinutesBeforeClose,
    [property: JsonPropertyName("flattenMinutesBeforeClose")] int FlattenMinutesBeforeClose);

/// <summary>Per-position invalidation. Percentages of the entry price, not of the account.</summary>
public sealed record ExitConfig(
    [property: JsonPropertyName("stopLossPercent")] decimal StopLossPercent,
    [property: JsonPropertyName("takeProfitPercent")] decimal TakeProfitPercent,
    [property: JsonPropertyName("maxHoldMinutes")] int MaxHoldMinutes);

public sealed record RiskConfig(
    [property: JsonPropertyName("maxPositionNotional")] decimal MaxPositionNotional,
    [property: JsonPropertyName("maxConcurrentPositions")] int MaxConcurrentPositions,
    [property: JsonPropertyName("maxDailyRealizedLoss")] decimal MaxDailyRealizedLoss,
    [property: JsonPropertyName("minimumCashReserve")] decimal MinimumCashReserve,
    [property: JsonPropertyName("maxPortfolioExposure")] decimal MaxPortfolioExposure,
    [property: JsonPropertyName("maxOrdersPerSymbolPerDay")] int MaxOrdersPerSymbolPerDay,
    [property: JsonPropertyName("maxTotalOrdersPerDay")] int MaxTotalOrdersPerDay,
    [property: JsonPropertyName("maxDataAgeSeconds")] int MaxDataAgeSeconds,
    [property: JsonPropertyName("pdtEquityThreshold")] decimal PdtEquityThreshold,
    [property: JsonPropertyName("maxDayTradesUnderPdtThreshold")] int MaxDayTradesUnderPdtThreshold);

/// <summary>Shape of config/symbols.json.</summary>
public sealed record SymbolConfigFile(
    [property: JsonPropertyName("allowlist")] IReadOnlyList<string> Allowlist,
    [property: JsonPropertyName("denylist")] IReadOnlyList<string> Denylist);

/// <summary>
/// Loads the two config files and turns them into the domain's own policy
/// types. Loaded once at startup: a trading policy that can change under a
/// running evaluation is a policy nobody can audit.
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
        var trading = Read<TradingConfigFile>(options.TradingConfigPath);
        var symbols = Read<SymbolConfigFile>(options.SymbolConfigPath);

        if (!string.Equals(trading.Environment, "paper", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"trading.json declares environment '{trading.Environment}'; only 'paper' is supported.");

        var strategy = Require(trading.Strategy, "strategy");
        var session = Require(trading.Session, "session");
        var exits = Require(trading.Exits, "exits");
        var risk = Require(trading.Risk, "risk");

        var allowed = symbols.Allowlist
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => s.Length > 0)
            .Except(symbols.Denylist.Select(s => s.Trim().ToUpperInvariant()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (allowed.Count == 0)
            throw new InvalidOperationException("The symbol allowlist is empty; there is nothing the agent may trade.");

        var maxDataAge = TimeSpan.FromSeconds(risk.MaxDataAgeSeconds);

        var sessionPolicy = new SessionPolicy(
            TimeSpan.FromMinutes(session.SkipFirstMinutesAfterOpen),
            TimeSpan.FromMinutes(session.NoNewEntriesMinutesBeforeClose),
            TimeSpan.FromMinutes(session.FlattenMinutesBeforeClose));

        var exitPolicy = new ExitPolicy(
            exits.StopLossPercent,
            exits.TakeProfitPercent,
            TimeSpan.FromMinutes(exits.MaxHoldMinutes));

        // Fail fast rather than starting with a session policy that would let
        // a position survive the close.
        var errors = sessionPolicy.Validate().Concat(exitPolicy.Validate()).ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Invalid day-trading policy in {options.TradingConfigPath}: {string.Join(" ", errors)}");

        return new TradingPolicySet
        {
            StrategyName = strategy.Name,
            LookbackBars = strategy.LookbackBars,
            Allowlist = allowed,
            Session = sessionPolicy,
            Exits = exitPolicy,
            Strategy = new MomentumPolicy(
                strategy.MinimumConfidence,
                strategy.MinimumVolumeRatio,
                strategy.MaximumSpreadBps,
                risk.MaxPositionNotional,
                maxDataAge),
            Risk = new RiskPolicy(
                risk.MaxPositionNotional,
                risk.MaxConcurrentPositions,
                risk.MaxDailyRealizedLoss,
                risk.MinimumCashReserve,
                risk.MaxPortfolioExposure,
                risk.MaxOrdersPerSymbolPerDay,
                risk.MaxTotalOrdersPerDay,
                maxDataAge,
                RequirePaperMode: true,
                // Both the file and the environment must agree before trading
                // is possible. Either one set to false is a kill switch.
                TradingEnabled: trading.TradingEnabled && options.TradingEnabled,
                PdtEquityThreshold: risk.PdtEquityThreshold,
                MaxDayTradesUnderPdt: risk.MaxDayTradesUnderPdtThreshold),
        };
    }

    private static T Require<T>(T? section, string name) where T : class =>
        section ?? throw new InvalidOperationException(
            $"trading.json is missing the required '{name}' section.");

    private static T Read<T>(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required configuration file not found: {path}");

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
               ?? throw new InvalidOperationException($"Configuration file {path} deserialised to null.");
    }
}
