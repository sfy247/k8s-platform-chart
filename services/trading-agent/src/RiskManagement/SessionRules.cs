using ClaudeTradingAgent.MarketData;

namespace ClaudeTradingAgent.RiskManagement;

public sealed record EntryWindow(bool IsOpen, string Code, string Reason)
{
    public static EntryWindow Allowed { get; } = new(true, "ENTRY_WINDOW_OPEN", "Inside the entry window.");
}

/// <summary>
/// Decides where inside the session the agent may open new positions.
///
/// Deliberately separate from the exit rules: an agent that is barred from
/// entering must still be able to exit, and folding both into one gate is how
/// a "no trading near the close" rule accidentally becomes "hold overnight".
/// </summary>
public static class SessionWindow
{
    public static EntryWindow EvaluateEntry(TradingSession session, SessionPolicy policy, DateTimeOffset now)
    {
        var elapsed = session.Elapsed(now);
        var remaining = session.Remaining(now);

        if (elapsed < TimeSpan.Zero)
            return new EntryWindow(false, "SESSION_NOT_STARTED", "The session has not opened yet.");

        if (elapsed < policy.NoEntryAfterOpen)
            return new EntryWindow(false, "OPENING_AUCTION",
                $"Within the first {policy.NoEntryAfterOpen.TotalMinutes:0} minutes of the session; spreads are unstable.");

        if (remaining <= TimeSpan.Zero)
            return new EntryWindow(false, "SESSION_ENDED", "The session has closed.");

        if (remaining <= policy.NoEntryBeforeClose)
            return new EntryWindow(false, "ENTRY_CUTOFF",
                $"Only {remaining.TotalMinutes:0} minutes remain; new entries stop {policy.NoEntryBeforeClose.TotalMinutes:0} minutes before the close.");

        return EntryWindow.Allowed;
    }

    /// <summary>True once every open position must be closed for the day.</summary>
    public static bool IsFlattenTime(TradingSession session, SessionPolicy policy, DateTimeOffset now) =>
        session.Remaining(now) <= policy.FlattenBeforeClose;
}

/// <summary>
/// Decides when an open position must be closed.
///
/// This is deterministic and lives in RiskManagement rather than in a
/// strategy on purpose. Exits are not an opinion about the market: the
/// strategy is allowed to be wrong about direction, but it is not allowed to
/// decide whether a stop applies or whether the position survives the close.
/// </summary>
public static class ExitManager
{
    public static ExitDecision Evaluate(
        PositionSnapshot position,
        ExitPolicy exitPolicy,
        SessionPolicy sessionPolicy,
        TradingSession session,
        DateTimeOffset now)
    {
        // Market value, not quantity: a position appearing in the broker's
        // positions list with a value is the signal that there is something
        // to close. Quantity is informational and may be absent.
        if (position.MarketValue <= 0) return ExitDecision.Hold;

        // Unconditional, and checked first so that no later rule — or bug in
        // one — can prevent the position being flat for the night.
        if (SessionWindow.IsFlattenTime(session, sessionPolicy, now))
        {
            var remaining = session.Remaining(now);
            return new ExitDecision(true, ExitReason.SessionClose,
                remaining <= TimeSpan.Zero
                    ? "The session has closed; day-trading positions are not carried overnight."
                    : $"{remaining.TotalMinutes:0} minutes to the close; flattening for the day.");
        }

        // No P&L from the broker means no stop and no target this cycle. The
        // alternative — treating a missing number as zero — would report every
        // position as flat and quietly switch the stops off.
        if (position.UnrealizedPnlFraction is { } fraction)
        {
            var pnlPercent = fraction * 100m;

            if (pnlPercent <= -exitPolicy.StopLossPercent)
                return new ExitDecision(true, ExitReason.StopLoss,
                    $"Down {pnlPercent:0.00}%, past the {exitPolicy.StopLossPercent:0.00}% stop.");

            if (pnlPercent >= exitPolicy.TakeProfitPercent)
                return new ExitDecision(true, ExitReason.TakeProfit,
                    $"Up {pnlPercent:0.00}%, at the {exitPolicy.TakeProfitPercent:0.00}% target.");
        }

        // Only when the broker's own fill history tells us when the position
        // was opened. An unknown open time means no max-hold check, never a
        // guessed one — the flatten deadline still bounds the hold.
        if (position.OpenedAtUtc is { } openedAt)
        {
            var held = now - openedAt;
            if (held >= exitPolicy.MaxHoldTime)
                return new ExitDecision(true, ExitReason.MaxHoldTime,
                    $"Held {held.TotalMinutes:0} minutes without reaching the stop or the target; the setup has expired.");
        }

        return ExitDecision.Hold;
    }
}
