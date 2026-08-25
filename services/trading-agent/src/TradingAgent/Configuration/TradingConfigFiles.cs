using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeTradingAgent.RiskManagement;
using ClaudeTradingAgent.Strategy;

namespace ClaudeTradingAgent.TradingAgent.Configuration;

/// <summary>Shape of config/trading.json.</summary>
public sealed record TradingConfigFile(
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("tradingEnabled")] bool TradingEnabled,
    [property: JsonPropertyName("strategy")] StrategyConfig Strategy,
    [property: JsonPropertyName("risk")] RiskConfig Risk);

public sealed record StrategyConfig(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("minimumConfidence")] decimal MinimumConfidence,
    [property: JsonPropertyName("lookbackBars")] int LookbackBars,
    [property: JsonPropertyName("minimumVolumeRatio")] decimal MinimumVolumeRatio,
    [property: JsonPropertyName("maximumSpreadBps")] decimal MaximumSpreadBps);

public sealed record RiskConfig(
    [property: JsonPropertyName("maxPositionNotional")] decimal MaxPositionNotional,
    [property: JsonPropertyName("maxConcurrentPositions")] int MaxConcurrentPositions,
    [property: JsonPropertyName("maxDailyRealizedLoss")] decimal MaxDailyRealizedLoss,
    [property: JsonPropertyName("minimumCashReserve")] decimal MinimumCashReserve,
    [property: JsonPropertyName("maxPortfolioExposure")] decimal MaxPortfolioExposure,
    [property: JsonPropertyName("maxOrdersPerSymbolPerDay")] int MaxOrdersPerSymbolPerDay,
    [property: JsonPropertyName("maxTotalOrdersPerDay")] int MaxTotalOrdersPerDay,
    [property: JsonPropertyName("maxDataAgeSeconds")] int MaxDataAgeSeconds);

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

        var allowed = symbols.Allowlist
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => s.Length > 0)
            .Except(symbols.Denylist.Select(s => s.Trim().ToUpperInvariant()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (allowed.Count == 0)
            throw new InvalidOperationException("The symbol allowlist is empty; there is nothing the agent may trade.");

        var maxDataAge = TimeSpan.FromSeconds(trading.Risk.MaxDataAgeSeconds);

        return new TradingPolicySet
        {
            StrategyName = trading.Strategy.Name,
            LookbackBars = trading.Strategy.LookbackBars,
            Allowlist = allowed,
            Strategy = new MomentumPolicy(
                trading.Strategy.MinimumConfidence,
                trading.Strategy.MinimumVolumeRatio,
                trading.Strategy.MaximumSpreadBps,
                trading.Risk.MaxPositionNotional,
                maxDataAge),
            Risk = new RiskPolicy(
                trading.Risk.MaxPositionNotional,
                trading.Risk.MaxConcurrentPositions,
                trading.Risk.MaxDailyRealizedLoss,
                trading.Risk.MinimumCashReserve,
                trading.Risk.MaxPortfolioExposure,
                trading.Risk.MaxOrdersPerSymbolPerDay,
                trading.Risk.MaxTotalOrdersPerDay,
                maxDataAge,
                RequirePaperMode: true,
                // Both the file and the environment must agree before trading
                // is possible. Either one set to false is a kill switch.
                TradingEnabled: trading.TradingEnabled && options.TradingEnabled),
        };
    }

    private static T Read<T>(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required configuration file not found: {path}");

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
               ?? throw new InvalidOperationException($"Configuration file {path} deserialised to null.");
    }
}
