namespace ClaudeTradingAgent.Indicators;

/// <summary>Bid/ask spread as a percentage of the midpoint.</summary>
public static class Spread
{
    /// <summary>
    /// spread = ask − bid; spreadPercent = spread ÷ ((bid + ask) / 2) × 100.
    /// Refuses a non-positive bid or ask, or an ask below the bid, rather than
    /// returning a number that looks valid.
    /// </summary>
    public static bool TryPercent(decimal bid, decimal ask, out decimal percent, out string? error)
    {
        percent = 0;
        if (bid <= 0 || ask <= 0) { error = "Bid and ask must both be positive."; return false; }
        if (ask < bid) { error = "Ask is below bid (crossed quote)."; return false; }
        percent = (ask - bid) / ((bid + ask) / 2m) * 100m;
        error = null;
        return true;
    }
}
