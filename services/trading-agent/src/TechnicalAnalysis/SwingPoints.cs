using ClaudeTradingAgent.MarketData;

namespace ClaudeTradingAgent.TechnicalAnalysis;

public enum SwingKind { High, Low }

/// <summary>
/// A confirmed swing. <see cref="Index"/> and <see cref="TimestampUtc"/> are
/// the swing candle; <see cref="ConfirmedAtUtc"/> is when enough later
/// candles had closed to know it was a swing — the earliest moment any
/// decision may use it.
/// </summary>
public sealed record SwingPoint(
    SwingKind Kind,
    int Index,
    DateTimeOffset TimestampUtc,
    decimal Price,
    int ConfirmedAtIndex,
    DateTimeOffset ConfirmedAtUtc);

/// <summary>
/// Finds swing highs and lows in the candles available at the evaluation time.
///
/// Candle i is a swing high when its high is strictly above the highs of the
/// <c>leftBars</c> candles before it and at or above the highs of the
/// <c>rightBars</c> candles after it (so a flat top of equal highs yields one
/// swing, at its first candle). Swing lows mirror this.
///
/// No look-ahead: a swing needs <c>rightBars</c> later candles, so the last
/// <c>rightBars</c> candles of the input can never be swings yet. Pass only
/// the candles that had closed at the time being evaluated.
/// </summary>
public sealed class SwingPointDetector(int leftBars = 2, int rightBars = 2)
{
    public int LeftBars { get; } = leftBars >= 1 ? leftBars : throw new ArgumentOutOfRangeException(nameof(leftBars));
    public int RightBars { get; } = rightBars >= 1 ? rightBars : throw new ArgumentOutOfRangeException(nameof(rightBars));

    public IReadOnlyList<SwingPoint> Detect(IReadOnlyList<Candle> candles)
    {
        var swings = new List<SwingPoint>();
        for (var i = LeftBars; i + RightBars < candles.Count; i++)
        {
            var confirmedAt = i + RightBars;
            if (IsSwing(candles, i, c => c.High, higher: true))
                swings.Add(new SwingPoint(SwingKind.High, i, candles[i].TimestampUtc, candles[i].High, confirmedAt, candles[confirmedAt].CloseTimeUtc));
            if (IsSwing(candles, i, c => c.Low, higher: false))
                swings.Add(new SwingPoint(SwingKind.Low, i, candles[i].TimestampUtc, candles[i].Low, confirmedAt, candles[confirmedAt].CloseTimeUtc));
        }
        return swings;
    }

    private bool IsSwing(IReadOnlyList<Candle> candles, int i, Func<Candle, decimal> price, bool higher)
    {
        var p = price(candles[i]);
        for (var j = i - LeftBars; j < i; j++)
            if (higher ? price(candles[j]) >= p : price(candles[j]) <= p) return false;
        for (var j = i + 1; j <= i + RightBars; j++)
            if (higher ? price(candles[j]) > p : price(candles[j]) < p) return false;
        return true;
    }
}
