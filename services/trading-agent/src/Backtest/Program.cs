using System.Globalization;
using System.Text.Json;
using ClaudeTradingAgent.Backtest;
using ClaudeTradingAgent.MarketData;
using ClaudeTradingAgent.RiskManagement;
using ClaudeTradingAgent.Strategy;

// Replays historical bars through the production strategy and risk engine
// and reports whether the result beat doing nothing.
//
//   dotnet run --project src/Backtest -- --symbols AAPL,MSFT --days 30
//
// Credentials come from the environment, the same two variables the agent
// uses. Nothing is written and no order is placed: this only reads bars.

var symbols = Arg("--symbols", "AAPL,MSFT,GOOGL,AMZN,NVDA").Split(',', StringSplitOptions.RemoveEmptyEntries);
var days = int.Parse(Arg("--days", "30"), CultureInfo.InvariantCulture);
var timeframe = Arg("--timeframe", "5Min");
var startingCash = decimal.Parse(Arg("--cash", "100"), CultureInfo.InvariantCulture);
var configPath = Arg("--config", "config/trading.json");

var key = Environment.GetEnvironmentVariable("ALPACA_API_KEY_ID");
var secret = Environment.GetEnvironmentVariable("ALPACA_API_SECRET_KEY");
if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
{
    Console.Error.WriteLine("ALPACA_API_KEY_ID and ALPACA_API_SECRET_KEY are required.");
    return 1;
}

// Policies come from the same file the running agent uses, so a backtest
// measures the configuration actually deployed.
using var configDoc = JsonDocument.Parse(File.ReadAllText(configPath));
var strategyCfg = configDoc.RootElement.GetProperty("strategy");
var riskCfg = configDoc.RootElement.GetProperty("risk");

var lookback = strategyCfg.GetProperty("lookbackBars").GetInt32();
var maxSpreadBps = strategyCfg.GetProperty("maximumSpreadBps").GetDecimal();
var maxDataAge = TimeSpan.FromDays(3650);   // irrelevant when replaying history

var strategyPolicy = new StrategyPolicy(
    strategyCfg.GetProperty("minimumConfidence").GetDecimal(),
    strategyCfg.GetProperty("minimumVolumeRatio").GetDecimal(),
    maxSpreadBps,
    riskCfg.GetProperty("maxPositionNotional").GetDecimal(),
    maxDataAge);

// The deployed limits are a production safety policy, not an evaluation
// tool. With $10 positions, $30 exposure and 8 orders a day against $100,
// a strategy gets 3-4 trades in a quarter — far too few to say anything
// about whether it has an edge. --unconstrained relaxes the caps so the
// SIGNAL can be measured; it says nothing about what should be deployed.
var unconstrained = Environment.GetCommandLineArgs().Contains("--unconstrained");

var riskPolicy = unconstrained
    ? new RiskPolicy(
        MaxPositionNotional: startingCash / 4m,
        MaxConcurrentPositions: 5,
        MaxDailyRealizedLoss: startingCash,      // no daily stop while measuring
        MinimumCashReserve: 0m,
        MaxPortfolioExposure: startingCash,      // allow fully invested
        MaxOrdersPerSymbolPerDay: 20,
        MaxTotalOrdersPerDay: 100,
        MaxDataAge: maxDataAge,
        RequirePaperMode: true,
        TradingEnabled: true,
        // The pattern-day-trader limit is a broker-enforced fact about a real
        // account, not a property of the signal. Zero disables the check so
        // the backtest measures the strategy rather than the account type.
        PdtEquityThreshold: 0m,
        MaxDayTradesUnderPdt: 0)
    : new RiskPolicy(
    riskCfg.GetProperty("maxPositionNotional").GetDecimal(),
    riskCfg.GetProperty("maxConcurrentPositions").GetInt32(),
    riskCfg.GetProperty("maxDailyRealizedLoss").GetDecimal(),
    riskCfg.GetProperty("minimumCashReserve").GetDecimal(),
    riskCfg.GetProperty("maxPortfolioExposure").GetDecimal(),
    riskCfg.GetProperty("maxOrdersPerSymbolPerDay").GetInt32(),
    riskCfg.GetProperty("maxTotalOrdersPerDay").GetInt32(),
    maxDataAge,
    RequirePaperMode: true,
    // The kill switch is a production control. A backtest that honoured it
    // would reject every proposal and report a flat line.
    TradingEnabled: true,
    PdtEquityThreshold: 0m,
    MaxDayTradesUnderPdt: 0);

if (unconstrained)
    Console.WriteLine("  MEASURING THE SIGNAL: risk caps relaxed. Not a deployable configuration.\n");

// The spread the strategy would actually pay. Bar data carries no bid/ask,
// so this is an assumption — and it is the assumption the result is most
// sensitive to, which is why it is a visible parameter rather than a
// constant buried in the code.
var assumedSpreadBps = decimal.Parse(Arg("--spread-bps", "5"), CultureInfo.InvariantCulture);

Console.WriteLine($"Backtest  symbols={string.Join(",", symbols)}  days={days}  timeframe={timeframe}");
Console.WriteLine($"          lookback={lookback} bars  minConfidence={strategyPolicy.MinimumConfidence}"
                  + $"  maxPosition=${riskPolicy.MaxPositionNotional}  assumedSpread={assumedSpreadBps}bps");
Console.WriteLine();

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.Add("APCA-API-KEY-ID", key);
http.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", secret);

var bars = new Dictionary<string, IReadOnlyList<Bar>>(StringComparer.OrdinalIgnoreCase);
var start = DateTimeOffset.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");

foreach (var symbol in symbols.Select(s => s.Trim().ToUpperInvariant()))
{
    var series = await FetchBarsAsync(http, symbol, timeframe, start);
    if (series.Count <= lookback + 1)
    {
        Console.Error.WriteLine($"  {symbol}: only {series.Count} bars, need more than {lookback + 1} — skipped.");
        continue;
    }
    bars[symbol] = series;
    Console.WriteLine($"  {symbol,-6} {series.Count,6} bars  "
                      + $"{series[0].TimestampUtc:yyyy-MM-dd} to {series[^1].TimestampUtc:yyyy-MM-dd}");
}

if (bars.Count == 0) { Console.Error.WriteLine("No usable data."); return 1; }

// Every strategy sees identical bars, identical costs and the identical
// risk engine. The only variable is the premise being tested.
ITradingStrategy[] strategies =
[
    new MomentumCrossoverStrategy(),
    new RsiMeanReversionStrategy(),
    new VwapReversionStrategy(),
    new OpeningRangeBreakoutStrategy(),
];

var wanted = Arg("--strategy", "all");
var selected = wanted == "all"
    ? strategies
    : strategies.Where(s => s.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToArray();

if (selected.Length == 0) { Console.Error.WriteLine($"No strategy matching '{wanted}'."); return 1; }

var results = new List<(ITradingStrategy Strategy, Result Result)>();
foreach (var s in selected)
{
    var r = new Simulation(s, strategyPolicy, riskPolicy, assumedSpreadBps).Run(bars, lookback, startingCash);
    results.Add((s, r));
    if (selected.Length == 1) Report(r);
}

Compare(results, startingCash);
return 0;

// ── helpers ──────────────────────────────────────────────────────────────
static string Arg(string name, string fallback)
{
    var args = Environment.GetCommandLineArgs();
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

static async Task<List<Bar>> FetchBarsAsync(HttpClient http, string symbol, string timeframe, string start)
{
    var all = new List<Bar>();
    string? pageToken = null;

    do
    {
        var url = $"https://data.alpaca.markets/v2/stocks/{symbol}/bars"
                  + $"?timeframe={timeframe}&start={start}&limit=10000&feed=iex&adjustment=all"
                  + (pageToken is null ? "" : $"&page_token={pageToken}");

        using var response = await http.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{symbol}: HTTP {(int)response.StatusCode} fetching bars.");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("bars", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            all.AddRange(arr.EnumerateArray().Select(b => new Bar(
                b.GetProperty("o").GetDecimal(), b.GetProperty("h").GetDecimal(),
                b.GetProperty("l").GetDecimal(), b.GetProperty("c").GetDecimal(),
                b.GetProperty("v").GetInt64(),
                DateTimeOffset.Parse(b.GetProperty("t").GetString()!, CultureInfo.InvariantCulture,
                                     DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal))));
        }

        pageToken = root.TryGetProperty("next_page_token", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() : null;
    }
    while (pageToken is not null);

    return all.OrderBy(b => b.TimestampUtc).ToList();
}

static void Compare(List<(ITradingStrategy Strategy, Result Result)> results, decimal startingCash)
{
    var hold = results[0].Result.BuyAndHoldEquity;
    Console.WriteLine();
    Console.WriteLine("══════════════════════════════════════════════════════════════════════════");
    Console.WriteLine($"  {"strategy",-24} {"return",9} {"vs hold",9} {"trades",7} {"win%",6} {"maxDD",7}");
    Console.WriteLine("  " + new string('-', 70));

    foreach (var (s, r) in results.OrderByDescending(x => x.Result.FinalEquity))
    {
        var ret = (r.FinalEquity - startingCash) / startingCash;
        var vs = (r.FinalEquity - hold) / startingCash;
        var trades = RoundTrips(r.Fills);
        var wins = trades.Count(p => p > 0);
        decimal peak = startingCash, dd = 0m;
        foreach (var e in r.EquityCurve) { peak = Math.Max(peak, e); dd = Math.Max(dd, peak == 0 ? 0 : (peak - e) / peak); }
        var winRate = trades.Count == 0 ? "-" : $"{(decimal)wins / trades.Count:P0}";
        Console.WriteLine($"  {s.Name,-24} {ret,9:P2} {vs,9:P2} {trades.Count,7} {winRate,6} {dd,7:P2}");
    }

    Console.WriteLine("  " + new string('-', 70));
    Console.WriteLine($"  {"buy and hold",-24} {(hold - startingCash) / startingCash,9:P2} {"—",9}");
    Console.WriteLine("══════════════════════════════════════════════════════════════════════════");
    Console.WriteLine();
    Console.WriteLine("  Premises tested:");
    foreach (var (s, _) in results) Console.WriteLine($"    {s.Name,-24} {s.Premise}");
    Console.WriteLine();
    Console.WriteLine("  Momentum and mean reversion are opposite bets. If both look");
    Console.WriteLine("  profitable on the same data, that is evidence of overfitting");
    Console.WriteLine("  rather than of two independent edges.");
}

static List<decimal> RoundTrips(IReadOnlyList<Fill> fills)
{
    var trades = new List<decimal>();
    var open = new Dictionary<string, (decimal Qty, decimal Cost)>(StringComparer.OrdinalIgnoreCase);
    foreach (var f in fills)
    {
        if (f.Action == TradeAction.Buy)
        {
            var (q, c) = open.GetValueOrDefault(f.Symbol, (0m, 0m));
            open[f.Symbol] = (q + f.Quantity, c + f.Notional);
        }
        else if (open.TryGetValue(f.Symbol, out var pos) && pos.Qty > 0)
        {
            var portion = Math.Min(f.Quantity / pos.Qty, 1m);
            var cost = pos.Cost * portion;
            trades.Add(f.Notional - cost);
            open[f.Symbol] = (pos.Qty - f.Quantity, pos.Cost - cost);
        }
    }
    return trades;
}

static void Report(Result r)
{
    var strategyReturn = (r.FinalEquity - r.StartingCash) / r.StartingCash;
    var holdReturn = (r.BuyAndHoldEquity - r.StartingCash) / r.StartingCash;

    // Round trips: a sell closes whatever the preceding buys opened.
    var trades = new List<decimal>();
    var open = new Dictionary<string, (decimal Qty, decimal Cost)>(StringComparer.OrdinalIgnoreCase);
    foreach (var f in r.Fills)
    {
        if (f.Action == TradeAction.Buy)
        {
            var (q, c) = open.GetValueOrDefault(f.Symbol, (0m, 0m));
            open[f.Symbol] = (q + f.Quantity, c + f.Notional);
        }
        else if (open.TryGetValue(f.Symbol, out var pos) && pos.Qty > 0)
        {
            var portion = Math.Min(f.Quantity / pos.Qty, 1m);
            var cost = pos.Cost * portion;
            trades.Add(f.Notional - cost);
            open[f.Symbol] = (pos.Qty - f.Quantity, pos.Cost - cost);
        }
    }

    var wins = trades.Where(p => p > 0).ToList();
    var losses = trades.Where(p => p <= 0).ToList();

    decimal peak = r.StartingCash, drawdown = 0m;
    foreach (var e in r.EquityCurve)
    {
        peak = Math.Max(peak, e);
        drawdown = Math.Max(drawdown, peak == 0 ? 0 : (peak - e) / peak);
    }

    Console.WriteLine();
    Console.WriteLine("──────────────────────────────────────────────────────────");
    Console.WriteLine($"  Bars evaluated        {r.BarsEvaluated:N0}");
    Console.WriteLine($"  Orders filled         {r.Fills.Count:N0}");
    Console.WriteLine($"  Completed round trips {trades.Count:N0}");
    Console.WriteLine();
    Console.WriteLine($"  Starting cash         ${r.StartingCash:N2}");
    Console.WriteLine($"  Strategy final        ${r.FinalEquity:N2}   ({strategyReturn:P2})");
    Console.WriteLine($"  Buy and hold final    ${r.BuyAndHoldEquity:N2}   ({holdReturn:P2})");
    Console.WriteLine($"  Strategy vs holding   {strategyReturn - holdReturn:P2}");
    Console.WriteLine();
    Console.WriteLine($"  Paid in spread        ${r.TotalSpreadCost:N2}"
                      + (r.StartingCash > 0 ? $"   ({r.TotalSpreadCost / r.StartingCash:P2} of capital)" : ""));
    Console.WriteLine($"  Max drawdown          {drawdown:P2}");

    if (trades.Count > 0)
    {
        Console.WriteLine($"  Win rate              {(decimal)wins.Count / trades.Count:P1}"
                          + $"  ({wins.Count}W / {losses.Count}L)");
        if (wins.Count > 0) Console.WriteLine($"  Average win           ${wins.Average():N4}");
        if (losses.Count > 0) Console.WriteLine($"  Average loss          ${losses.Average():N4}");
    }

    Console.WriteLine();
    Console.WriteLine("  Decision outcomes");
    foreach (var (code, count) in r.Rejections.OrderByDescending(kv => kv.Value))
        Console.WriteLine($"    {code,-24} {count,8:N0}");

    Console.WriteLine("──────────────────────────────────────────────────────────");
    Console.WriteLine();
    Console.WriteLine("  Bar data has no bid/ask, so the spread is assumed rather than");
    Console.WriteLine("  observed, and fills assume the full order completes at the next");
    Console.WriteLine("  open. Both flatter the strategy. Treat a result that only just");
    Console.WriteLine("  beats holding as a result that does not.");
}
