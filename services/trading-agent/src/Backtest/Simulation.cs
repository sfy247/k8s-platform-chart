using ClaudeTradingAgent.MarketData;
using ClaudeTradingAgent.RiskManagement;
using ClaudeTradingAgent.Strategy;

namespace ClaudeTradingAgent.Backtest;

public sealed record Fill(
    DateTimeOffset At, string Symbol, TradeAction Action,
    decimal Price, decimal Quantity, decimal Notional, decimal SpreadCost);

public sealed record Result(
    decimal StartingCash,
    decimal FinalEquity,
    decimal BuyAndHoldEquity,
    IReadOnlyList<Fill> Fills,
    IReadOnlyList<decimal> EquityCurve,
    IReadOnlyDictionary<string, int> Rejections,
    decimal TotalSpreadCost,
    int BarsEvaluated);

/// <summary>
/// Replays historical bars through the production strategy and risk engine.
///
/// Two properties matter more than anything else here, because getting either
/// wrong produces a backtest that looks profitable and is fiction:
///
///   No look-ahead. A signal computed from the bar closing at time T is
///   filled at the OPEN of the bar after it. Filling at T's close means
///   trading on a price you could not have known, which is the single most
///   common way a backtest lies.
///
///   Costs are paid. Every entry and exit crosses the spread. A strategy
///   trading a 0.5% round-trip cost has to be right by more than 0.5% before
///   it has made anything, and ignoring that is the second most common lie.
/// </summary>
public sealed class Simulation(MomentumPolicy strategyPolicy, RiskPolicy riskPolicy, decimal spreadBps)
{
    private readonly MomentumStrategy _strategy = new();
    private readonly RiskEngine _risk = new();

    public Result Run(
        IReadOnlyDictionary<string, IReadOnlyList<Bar>> barsBySymbol,
        int lookback,
        decimal startingCash)
    {
        var allowlist = barsBySymbol.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cash = startingCash;
        var shares = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var fills = new List<Fill>();
        var equityCurve = new List<decimal>();
        var rejections = new Dictionary<string, int>(StringComparer.Ordinal);
        var ordersToday = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var totalOrdersToday = 0;
        var dayStartEquity = startingCash;
        DateTime currentDay = default;
        var barsEvaluated = 0;

        // One timeline across all symbols, so the account state a symbol sees
        // reflects trades already made in other symbols on the same bar.
        var timeline = barsBySymbol
            .SelectMany(kv => kv.Value.Select((bar, i) => (Symbol: kv.Key, Bar: bar, Index: i)))
            .OrderBy(x => x.Bar.TimestampUtc)
            .ToList();

        foreach (var (symbol, bar, index) in timeline)
        {
            var series = barsBySymbol[symbol];

            // Need a full lookback window, and a NEXT bar to fill against.
            if (index < lookback || index + 1 >= series.Count) continue;
            barsEvaluated++;

            if (bar.TimestampUtc.UtcDateTime.Date != currentDay)
            {
                currentDay = bar.TimestampUtc.UtcDateTime.Date;
                ordersToday.Clear();
                totalOrdersToday = 0;
                dayStartEquity = Equity(cash, shares, LastPrices(barsBySymbol, bar.TimestampUtc));
            }

            var window = series.Skip(index - lookback + 1).Take(lookback).ToList();
            var inputs = BuildInputs(symbol, window, spreadBps);
            var proposal = _strategy.Evaluate(inputs, strategyPolicy, bar.TimestampUtc);

            var prices = LastPrices(barsBySymbol, bar.TimestampUtc);
            var equity = Equity(cash, shares, prices);
            var held = shares.GetValueOrDefault(symbol, 0m);
            var heldNotional = held * bar.Close;

            var state = new AccountRiskState(
                Cash: cash,
                PortfolioExposure: equity - cash,
                DailyRealizedPnl: equity - dayStartEquity,
                OpenPositionCount: shares.Count(kv => kv.Value > 0),
                TotalOrdersToday: totalOrdersToday,
                OrdersForSymbolToday: ordersToday.GetValueOrDefault(symbol, 0),
                MarketOpen: true,
                IsPaperEndpoint: true,
                HasOpenOrderForSymbol: false,
                ExistingPositionNotional: heldNotional);

            var decision = _risk.Evaluate(proposal, state, riskPolicy, allowlist, bar.TimestampUtc);

            if (!decision.Approved || decision.Order is null)
            {
                rejections[decision.Code] = rejections.GetValueOrDefault(decision.Code) + 1;
                equityCurve.Add(equity);
                continue;
            }

            // Fill at the NEXT bar's open — the first price actually
            // reachable after the signal existed.
            var fillBar = series[index + 1];
            var half = spreadBps / 10_000m / 2m;
            var order = decision.Order;

            // Buying lifts the offer, selling hits the bid. Either way the
            // spread is paid, never earned.
            var fillPrice = order.Action == TradeAction.Buy
                ? fillBar.Open * (1 + half)
                : fillBar.Open * (1 - half);

            var notional = order.Action == TradeAction.Buy
                ? Math.Min(order.Notional, cash)
                : Math.Min(order.Notional, heldNotional);

            if (notional <= 0.01m) { equityCurve.Add(equity); continue; }

            var quantity = notional / fillPrice;
            var spreadCost = fillBar.Open * half * quantity;

            if (order.Action == TradeAction.Buy)
            {
                cash -= notional;
                shares[symbol] = held + quantity;
            }
            else
            {
                cash += notional;
                var remaining = held - quantity;
                if (remaining <= 0.000001m) shares.Remove(symbol); else shares[symbol] = remaining;
            }

            fills.Add(new Fill(fillBar.TimestampUtc, symbol, order.Action, fillPrice, quantity, notional, spreadCost));
            ordersToday[symbol] = ordersToday.GetValueOrDefault(symbol) + 1;
            totalOrdersToday++;
            rejections["APPROVED"] = rejections.GetValueOrDefault("APPROVED") + 1;
            equityCurve.Add(Equity(cash, shares, LastPrices(barsBySymbol, bar.TimestampUtc)));
        }

        var finalPrices = barsBySymbol.ToDictionary(kv => kv.Key, kv => kv.Value[^1].Close, StringComparer.OrdinalIgnoreCase);
        var finalEquity = Equity(cash, shares, finalPrices);

        // The benchmark that matters: split the same cash evenly across the
        // same symbols on day one and do nothing at all.
        var perSymbol = startingCash / barsBySymbol.Count;
        var buyAndHold = barsBySymbol.Sum(kv => perSymbol / kv.Value[0].Open * kv.Value[^1].Close);

        return new Result(startingCash, finalEquity, buyAndHold, fills, equityCurve,
                          rejections, fills.Sum(f => f.SpreadCost), barsEvaluated);
    }

    private static Dictionary<string, decimal> LastPrices(
        IReadOnlyDictionary<string, IReadOnlyList<Bar>> bars, DateTimeOffset at)
    {
        var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var (symbol, series) in bars)
        {
            var last = series.LastOrDefault(b => b.TimestampUtc <= at) ?? series[0];
            prices[symbol] = last.Close;
        }
        return prices;
    }

    private static decimal Equity(decimal cash, Dictionary<string, decimal> shares, Dictionary<string, decimal> prices)
        => cash + shares.Sum(kv => kv.Value * prices.GetValueOrDefault(kv.Key, 0m));

    /// <summary>
    /// Mirrors the worker's own input construction, so the backtest feeds the
    /// strategy exactly what production feeds it.
    /// </summary>
    private static MomentumInputs BuildInputs(string symbol, IReadOnlyList<Bar> window, decimal spreadBps)
    {
        var closes = window.Select(b => b.Close).ToArray();
        var fastWindow = Math.Min(5, closes.Length);
        var fast = closes.TakeLast(fastWindow).Average();
        var slow = closes.Average();

        var volumes = window.Select(b => (decimal)b.Volume).ToArray();
        var recent = volumes.TakeLast(fastWindow).Average();
        var average = volumes.Average();

        return new MomentumInputs(
            symbol.ToUpperInvariant(),
            window[^1].Close,
            fast,
            slow,
            average <= 0 ? 0m : recent / average,
            spreadBps,
            window[^1].TimestampUtc);
    }
}
