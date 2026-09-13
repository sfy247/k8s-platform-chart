using System.Text.Json.Nodes;
using ClaudeTradingAgent.TradingAgent.Configuration;
using Xunit;

namespace ClaudeTradingAgent.Tests;

/// <summary>
/// Loads the policy files that ship inside the image. They are baked in, so a
/// mistake here is a crash-looping pod after a deploy, not a CI failure — and
/// a flag the code silently ignored would be worse than either.
/// </summary>
public sealed class TradingPolicySetTests
{
    private static readonly string Shipped = Path.Combine(AppContext.BaseDirectory, "config", "trading.json");

    private static AgentOptions Options(string? tradingPath = null, bool tradingEnabled = true) => new()
    {
        TradingMode = "PAPER",
        TradingEnabled = tradingEnabled,
        AlpacaApiKeyId = "test",
        AlpacaApiSecretKey = "test",
        TradingConfigPath = tradingPath ?? Shipped,
        SymbolConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "symbols.json"),
    };

    /// <summary>Loads the shipped config with one change applied.</summary>
    private static InvalidOperationException Refuses(Action<JsonObject> change)
    {
        var json = JsonNode.Parse(File.ReadAllText(Shipped))!.AsObject();
        change(json);
        var path = Path.Combine(Path.GetTempPath(), $"trading-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json.ToJsonString());
        try { return Assert.Throws<InvalidOperationException>(() => TradingPolicySet.Load(Options(path))); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_shipped_configuration_carries_the_v3_limits()
    {
        var p = TradingPolicySet.Load(Options());

        Assert.Equal(100m, p.Risk.StrategyCapital);
        Assert.Equal(10m, p.Risk.MaxNotionalPerTrade);
        Assert.Equal(2, p.Risk.MaxConcurrentPositions);
        Assert.Equal(20m, p.Risk.MaxTotalExposure);
        Assert.Equal(3m, p.Risk.MaxDailyLoss);
        Assert.Equal(1m, p.Risk.MaxEstimatedLossPerTrade);
        Assert.Equal(TimeSpan.FromSeconds(10), p.Risk.MaxDataAge);
        Assert.Equal(new TimeOnly(9, 35), p.Session.EntryStartEt);
        Assert.Equal(new TimeOnly(15, 30), p.Session.EntryCutoffEt);
        Assert.Equal(new TimeOnly(15, 55), p.Session.FlattenEt);
        Assert.NotEmpty(p.Allowlist);
    }

    [Fact]
    public void Either_kill_switch_disables_trading() =>
        Assert.False(TradingPolicySet.Load(Options(tradingEnabled: false)).Risk.TradingEnabled);

    [Theory]
    [InlineData("allowMargin")]
    [InlineData("allowShortSelling")]
    [InlineData("allowOptions")]
    [InlineData("allowCrypto")]
    [InlineData("allowOvernightPositions")]
    [InlineData("allowExtendedHours")]
    public void Refuses_to_start_with_an_unsupported_feature_switched_on(string flag) =>
        Assert.Contains(flag, Refuses(c => c[flag] = true).Message);

    [Fact] public void Refuses_a_non_regular_hours_session() => Assert.Contains("regularHoursOnly", Refuses(c => c["regularHoursOnly"] = false).Message);
    [Fact] public void Refuses_a_time_not_in_HHmm_form() => Assert.Contains("flattenTimeEt", Refuses(c => c["flattenTimeEt"] = "3:55pm").Message);
    [Fact] public void Refuses_a_flatten_before_the_entry_cutoff() => Assert.Contains("flattenTimeEt", Refuses(c => c["flattenTimeEt"] = "15:15").Message);
    [Fact] public void Refuses_exposure_beyond_strategy_capital() => Assert.Contains("strategyCapital", Refuses(c => c["maxTotalExposure"] = 500).Message);
    [Fact] public void Refuses_a_per_trade_loss_limit_every_trade_would_breach() => Assert.Contains("maxEstimatedLossPerTrade", Refuses(c => c["maxEstimatedLossPerTrade"] = 0.01).Message);
    [Fact] public void Names_a_missing_section() => Assert.Contains("exits", Refuses(c => c.Remove("exits")).Message);

    [Fact]
    public void Reports_every_problem_at_once()
    {
        var message = Refuses(c => { c["allowMargin"] = true; c["maxDailyLoss"] = 0; }).Message;
        Assert.Contains("allowMargin", message);
        Assert.Contains("maxDailyLoss", message);
    }
}
