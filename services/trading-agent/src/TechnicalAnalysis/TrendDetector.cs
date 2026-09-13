using ClaudeTradingAgent.Indicators;
using ClaudeTradingAgent.MarketData;

namespace ClaudeTradingAgent.TechnicalAnalysis;

public enum TrendDirection { Undetermined, Bullish, Bearish, Neutral }

public enum TrendStrength { None, Weak, Moderate, Strong }

public sealed record TrendAssessment(
    TrendDirection Direction,
    TrendStrength Strength,
    int Score,
    int ConfidenceScore,
    IReadOnlyList<string> Evidence)
{
    public string DirectionLabel => Direction.ToString().ToUpperInvariant();
    public string StrengthLabel => Strength.ToString().ToUpperInvariant();

    public static TrendAssessment Undetermined(string reason) =>
        new(TrendDirection.Undetermined, TrendStrength.None, 0, 0, [reason]);
}

/// <summary>
/// Scores the trend of the latest candle from evidence available at its close.
///
/// Each item adds or subtracts fixed points; equal readings and unavailable
/// indicators add nothing:
///
///   close vs session VWAP              ±15
///   EMA fast vs EMA slow               ±20
///   EMA slow vs EMA trend              ±15
///   EMA slow slope over SlopeLookback  ±15
///   market structure                   ±25
///   close vs close MomentumLookback ago ±10
///
/// Score −100..100. ≥ +25 BULLISH, ≤ −25 BEARISH, otherwise NEUTRAL.
/// Confidence is |score|; strength STRONG ≥ 70, MODERATE ≥ 45, WEAK ≥ 25.
/// Same candles, same settings → same result, always.
/// </summary>
public sealed class TrendDetector(TechnicalAnalysisSettings? settings = null)
{
    private readonly TechnicalAnalysisSettings _s = settings ?? TechnicalAnalysisSettings.Default;

    public TrendAssessment Assess(IReadOnlyList<Candle> candles)
    {
        var minimum = _s.EmaSlowPeriod + _s.SlopeLookback;
        if (candles.Count < minimum)
            return TrendAssessment.Undetermined($"Needs at least {minimum} candles; have {candles.Count}.");

        var closes = CandleValues.Closes(candles);
        var last = candles.Count - 1;
        var close = closes[last];
        var emaFast = MovingAverage.Ema(closes, _s.EmaFastPeriod);
        var emaSlow = MovingAverage.Ema(closes, _s.EmaSlowPeriod);
        var emaTrend = MovingAverage.Ema(closes, _s.EmaTrendPeriod);
        var vwap = SessionVwap.Calculate(candles)[last];
        var swings = new SwingPointDetector(_s.SwingLeftBars, _s.SwingRightBars).Detect(candles);
        var structure = new MarketStructureDetector(_s.EqualSwingTolerancePercent).Classify(swings);

        var score = 0;
        var evidence = new List<string>();

        void Add(decimal? a, decimal? b, int points, string above, string below, string missing)
        {
            if (a is null || b is null) { evidence.Add(missing); return; }
            if (a > b) { score += points; evidence.Add($"{above} (+{points})"); }
            else if (a < b) { score -= points; evidence.Add($"{below} (−{points})"); }
        }

        Add(close, vwap, 15, "Price above VWAP", "Price below VWAP", "VWAP unavailable");
        Add(emaFast[last], emaSlow[last], 20, $"EMA{_s.EmaFastPeriod} > EMA{_s.EmaSlowPeriod}", $"EMA{_s.EmaFastPeriod} < EMA{_s.EmaSlowPeriod}", "Fast/slow EMA unavailable");
        Add(emaSlow[last], emaTrend[last], 15, $"EMA{_s.EmaSlowPeriod} > EMA{_s.EmaTrendPeriod}", $"EMA{_s.EmaSlowPeriod} < EMA{_s.EmaTrendPeriod}", $"EMA{_s.EmaTrendPeriod} unavailable (fewer than {_s.EmaTrendPeriod} candles)");
        Add(emaSlow[last], emaSlow[last - _s.SlopeLookback], 15, $"EMA{_s.EmaSlowPeriod} rising", $"EMA{_s.EmaSlowPeriod} falling", $"EMA{_s.EmaSlowPeriod} slope unavailable");

        switch (structure.State)
        {
            case StructureState.Bullish: score += 25; evidence.Add("Higher highs and higher lows (+25)"); break;
            case StructureState.Bearish: score -= 25; evidence.Add("Lower highs and lower lows (−25)"); break;
            case StructureState.Range: evidence.Add("Structure is a range"); break;
            default: evidence.Add("Structure undetermined"); break;
        }

        Add(close, last >= _s.MomentumLookback ? closes[last - _s.MomentumLookback] : null, 10,
            $"Close above {_s.MomentumLookback} candles ago", $"Close below {_s.MomentumLookback} candles ago", "Momentum lookback unavailable");

        var confidence = Math.Abs(score);
        var direction = score >= 25 ? TrendDirection.Bullish : score <= -25 ? TrendDirection.Bearish : TrendDirection.Neutral;
        var strength = confidence >= 70 ? TrendStrength.Strong : confidence >= 45 ? TrendStrength.Moderate : confidence >= 25 ? TrendStrength.Weak : TrendStrength.None;
        return new TrendAssessment(direction, strength, score, confidence, evidence);
    }
}
