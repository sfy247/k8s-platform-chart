namespace ClaudeTradingAgent.TechnicalAnalysis;

public enum StructureState { Undetermined, Bullish, Bearish, Range }

public enum SwingComparison { Higher, Lower, Equal }

public sealed record MarketStructure(
    StructureState State,
    SwingComparison? Highs,
    SwingComparison? Lows,
    SwingPoint? LastHigh,
    SwingPoint? LastLow,
    IReadOnlyList<string> Evidence)
{
    public string Label => State switch
    {
        StructureState.Bullish => "BULLISH",
        StructureState.Bearish => "BEARISH",
        StructureState.Range => "RANGE",
        _ => "UNDETERMINED",
    };
}

/// <summary>
/// Classifies market structure from the two most recent confirmed swing highs
/// and the two most recent confirmed swing lows:
///
///   higher high + higher low  → BULLISH
///   lower high  + lower low   → BEARISH
///   any other combination     → RANGE (including equal highs or lows)
///   fewer than two of either  → UNDETERMINED
///
/// Two swings count as equal when within <c>equalTolerancePercent</c> of the
/// earlier one. Deterministic code, never a model.
/// </summary>
public sealed class MarketStructureDetector(decimal equalTolerancePercent = 0m)
{
    public MarketStructure Classify(IReadOnlyList<SwingPoint> swings)
    {
        var highs = swings.Where(s => s.Kind == SwingKind.High).OrderBy(s => s.Index).ToList();
        var lows = swings.Where(s => s.Kind == SwingKind.Low).OrderBy(s => s.Index).ToList();
        var lastHigh = highs.LastOrDefault();
        var lastLow = lows.LastOrDefault();

        if (highs.Count < 2 || lows.Count < 2)
            return new MarketStructure(StructureState.Undetermined, null, null, lastHigh, lastLow,
                [$"Needs two confirmed swing highs and two swing lows; have {highs.Count} and {lows.Count}."]);

        var highCmp = Compare(highs[^1].Price, highs[^2].Price);
        var lowCmp = Compare(lows[^1].Price, lows[^2].Price);

        var evidence = new List<string>
        {
            $"{Name(highCmp, "high")}: {highs[^2].Price:0.####} → {highs[^1].Price:0.####}",
            $"{Name(lowCmp, "low")}: {lows[^2].Price:0.####} → {lows[^1].Price:0.####}",
        };

        var state = (highCmp, lowCmp) switch
        {
            (SwingComparison.Higher, SwingComparison.Higher) => StructureState.Bullish,
            (SwingComparison.Lower, SwingComparison.Lower) => StructureState.Bearish,
            _ => StructureState.Range,
        };
        return new MarketStructure(state, highCmp, lowCmp, lastHigh, lastLow, evidence);
    }

    private SwingComparison Compare(decimal latest, decimal previous)
    {
        var tolerance = Math.Abs(previous) * equalTolerancePercent / 100m;
        if (Math.Abs(latest - previous) <= tolerance) return SwingComparison.Equal;
        return latest > previous ? SwingComparison.Higher : SwingComparison.Lower;
    }

    private static string Name(SwingComparison cmp, string what) => cmp switch
    {
        SwingComparison.Higher => $"Higher {what}",
        SwingComparison.Lower => $"Lower {what}",
        _ => $"Equal {what}",
    };
}
