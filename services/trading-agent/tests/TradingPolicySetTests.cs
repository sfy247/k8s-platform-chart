using ClaudeTradingAgent.TradingAgent.Configuration;
using Xunit;

namespace ClaudeTradingAgent.Tests;

/// <summary>
/// Loads the policy files that ship inside the image.
///
/// These are baked into the container, so a section the loader requires but
/// the file does not have is not a test failure in CI — it is a pod that
/// crash-loops on startup after a deploy. Catching it here is the cheap
/// version of that discovery.
/// </summary>
public sealed class TradingPolicySetTests
{
    private static AgentOptions Options() => new()
    {
        TradingMode = "PAPER",
        TradingEnabled = true,
        AlpacaApiKeyId = "test",
        AlpacaApiSecretKey = "test",
        TradingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "trading.json"),
        SymbolConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "symbols.json"),
    };

    [Fact]
    public void The_shipped_configuration_loads()
    {
        var policies = TradingPolicySet.Load(Options());

        Assert.NotEmpty(policies.Allowlist);
        Assert.True(policies.Risk.MaxPositionNotional > 0);
        Assert.Empty(policies.Session.Validate());
        Assert.Empty(policies.Exits.Validate());
    }

    [Fact]
    public void The_shipped_configuration_promises_to_be_flat_overnight()
    {
        var policies = TradingPolicySet.Load(Options());

        Assert.True(policies.Session.FlattenBeforeClose > TimeSpan.Zero);
        Assert.True(policies.Session.NoEntryBeforeClose >= policies.Session.FlattenBeforeClose);
    }

    [Fact]
    public void The_shipped_configuration_allows_more_than_one_round_trip_per_symbol()
    {
        // A day-trading agent capped at two orders per symbol per day gets a
        // single round trip and then sits out. That limit is what the file
        // used to carry, and it quietly made day trading impossible.
        var policies = TradingPolicySet.Load(Options());
        Assert.True(policies.Risk.MaxOrdersPerSymbolPerDay >= 4);
    }

    [Fact]
    public void A_missing_section_fails_with_a_message_that_names_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trading-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "environment": "paper", "tradingEnabled": false,
              "strategy": { "name": "x", "minimumConfidence": 0.7, "lookbackBars": 20,
                            "minimumVolumeRatio": 1.2, "maximumSpreadBps": 25 } }
            """);
        try
        {
            var options = Options() with { TradingConfigPath = path };
            var error = Assert.Throws<InvalidOperationException>(() => TradingPolicySet.Load(options));
            Assert.Contains("session", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
