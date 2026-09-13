namespace ClaudeTradingAgent.TechnicalAnalysis;

/// <summary>
/// Every tunable number in the analysis, in one place. Periods are never
/// hard-coded inside a calculator; they come from here.
/// </summary>
public sealed record TechnicalAnalysisSettings
{
    public int EmaFastPeriod { get; init; } = 9;
    public int EmaSlowPeriod { get; init; } = 20;
    public int EmaTrendPeriod { get; init; } = 50;
    public int SmaPeriod { get; init; } = 20;
    public int AtrPeriod { get; init; } = 14;

    public int SwingLeftBars { get; init; } = 2;
    public int SwingRightBars { get; init; } = 2;
    public decimal EqualSwingTolerancePercent { get; init; } = 0m;

    /// <summary>Candles back for the slow-EMA slope comparison.</summary>
    public int SlopeLookback { get; init; } = 5;

    /// <summary>Candles back for the close-to-close momentum comparison.</summary>
    public int MomentumLookback { get; init; } = 10;

    /// <summary>Lookback for trailing relative volume (see RelativeVolume.Trailing).</summary>
    public int RelativeVolumeLookback { get; init; } = 20;

    /// <summary>Swing prices within this percent of a level's mean join that level.</summary>
    public decimal LevelTolerancePercent { get; init; } = 0.10m;

    /// <summary>A close must clear the level by this percent to count as a breakout.</summary>
    public decimal BreakoutBufferPercent { get; init; } = 0.05m;
    public decimal BreakoutMinRelativeVolume { get; init; } = 1.5m;

    /// <summary>When true a wick beyond the level is not enough; the candle must close beyond it.</summary>
    public bool BreakoutRequireClose { get; init; } = true;

    /// <summary>Optional: a breakout is not confirmed while the spread is wider than this.</summary>
    public decimal? BreakoutMaxSpreadPercent { get; init; }

    public static TechnicalAnalysisSettings Default { get; } = new();
}
