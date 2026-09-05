using ClaudeTradingAgent.TradingAgent.Configuration;
using Xunit;

namespace TradingAgent.Tests;

/// <summary>
/// Configuration is a safety control here, not a convenience: these are the
/// checks that stop the agent starting against the wrong endpoint or in the
/// wrong mode.
/// </summary>
public sealed class AgentOptionsTests
{
    private static AgentOptions Valid() => new()
    {
        TradingMode = "PAPER",
        TradingEnabled = false,
        AlpacaTradingBaseUrl = "https://paper-api.alpaca.markets",
        EvaluationIntervalSeconds = 60,
        BrokerTimeoutSeconds = 10,
    };

    [Fact]
    public void DefaultConfigurationIsValid()
        => Assert.Empty(Valid().Validate());

    // ── Market data feed ─────────────────────────────────────────────────

    [Fact]
    public void DefaultsToTheFreeFeed()
    {
        // Free by default so a fresh checkout runs without a paid data plan.
        Assert.Equal("iex", new AgentOptions().NormalisedDataFeed);
    }

    [Theory]
    [InlineData("iex")]
    [InlineData("sip")]
    [InlineData("SIP")]
    [InlineData("  sip  ")]
    public void AcceptsTheSupportedFeeds(string feed)
        => Assert.Empty((Valid() with { AlpacaDataFeed = feed }).Validate());

    [Theory]
    [InlineData("delayed_sip")]
    [InlineData("otc")]
    [InlineData("")]
    [InlineData("nonsense")]
    public void RejectsFeedsThatAreNotSuitableForIntradayTrading(string feed)
    {
        // A delayed feed would be caught later by the staleness check, but
        // failing at startup says why; failing at the staleness check just
        // looks like an agent that never trades.
        var errors = (Valid() with { AlpacaDataFeed = feed }).Validate();
        Assert.Contains(errors, e => e.Contains("ALPACA_DATA_FEED"));
    }

    [Fact]
    public void NormalisesTheFeedForTheApi()
        => Assert.Equal("sip", (Valid() with { AlpacaDataFeed = " SIP " }).NormalisedDataFeed);

    [Fact]
    public void DefaultsToPaperAndDisabled()
    {
        var options = new AgentOptions();
        Assert.Equal("PAPER", options.TradingMode);
        Assert.False(options.TradingEnabled);
    }

    [Fact]
    public void LiveModeIsRejected()
    {
        var errors = (Valid() with { TradingMode = "LIVE" }).Validate();
        Assert.Contains(errors, e => e.Contains("PAPER"));
    }

    [Theory]
    [InlineData("https://api.alpaca.markets")]          // the LIVE endpoint
    [InlineData("http://paper-api.alpaca.markets")]     // not HTTPS
    [InlineData("https://evil.example.com")]
    [InlineData("not-a-url")]
    public void OnlyThePaperEndpointOverHttpsIsAccepted(string url)
    {
        var errors = (Valid() with { AlpacaTradingBaseUrl = url }).Validate();
        Assert.Contains(errors, e => e.Contains("ALPACA_TRADING_BASE_URL"));
    }

    [Fact]
    public void EnablingTradingWithoutCredentialsIsRejected()
    {
        var errors = (Valid() with { TradingEnabled = true }).Validate();
        Assert.Contains(errors, e => e.Contains("credentials"));
    }

    [Fact]
    public void EnablingTradingWithCredentialsIsAccepted()
    {
        var errors = (Valid() with
        {
            TradingEnabled = true,
            AlpacaApiKeyId = "key",
            AlpacaApiSecretKey = "secret",
        }).Validate();
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    public void TooFrequentEvaluationIsRejected(int seconds)
        => Assert.Contains((Valid() with { EvaluationIntervalSeconds = seconds }).Validate(),
                           e => e.Contains("EVALUATION_INTERVAL_SECONDS"));
}
