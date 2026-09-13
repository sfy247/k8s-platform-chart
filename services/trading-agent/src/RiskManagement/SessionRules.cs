using ClaudeTradingAgent.MarketData;

namespace ClaudeTradingAgent.RiskManagement;

/// <summary>The session's boundaries for one day, as absolute instants.</summary>
public sealed record SessionSchedule(
    DateTimeOffset OpenUtc,
    DateTimeOffset EntryStartUtc,
    DateTimeOffset EntryCutoffUtc,
    DateTimeOffset FlattenUtc,
    DateTimeOffset CloseUtc);

/// <summary>
/// Turns the configured New York clock times into today's schedule and
/// resolves which session state the agent is in.
///
/// Entry start keeps its distance from the open; the cutoff and the flatten
/// keep their distance from the close. On a regular day that is exactly the
/// configured clock time. On an early close it moves with the close, so the
/// flatten can never land after the market has shut.
/// </summary>
public static class SessionWindow
{
    public static SessionSchedule Schedule(TradingSession session, SessionPolicy policy)
    {
        var entryStart = session.OpenUtc + (policy.EntryStartEt - SessionPolicy.RegularOpenEt);
        var cutoff = session.CloseUtc - (SessionPolicy.RegularCloseEt - policy.EntryCutoffEt);
        var flatten = session.CloseUtc - (SessionPolicy.RegularCloseEt - policy.FlattenEt);

        // A session too short to hold an entry window simply has none.
        if (cutoff < entryStart) cutoff = entryStart;
        if (flatten < cutoff) flatten = cutoff;

        return new SessionSchedule(session.OpenUtc, entryStart, cutoff, flatten, session.CloseUtc);
    }

    public static SessionState Resolve(bool marketOpen, SessionSchedule schedule, DateTimeOffset now)
    {
        if (!marketOpen) return SessionState.MarketClosed;
        if (now < schedule.EntryStartUtc) return SessionState.PreMarketDisabled;
        if (now < schedule.EntryCutoffUtc) return SessionState.EntryWindow;
        if (now < schedule.FlattenUtc) return SessionState.ManagementOnly;

        // Past the flatten time while the clock still says open — including
        // any skew past the close — keeps trying to get flat.
        return SessionState.FlattenWindow;
    }

    public static string Label(SessionState state) => state switch
    {
        SessionState.PreMarketDisabled => "PRE_MARKET_DISABLED",
        SessionState.EntryWindow => "ENTRY_WINDOW",
        SessionState.ManagementOnly => "MANAGEMENT_ONLY",
        SessionState.FlattenWindow => "FLATTEN_WINDOW",
        _ => "MARKET_CLOSED",
    };
}

/// <summary>
/// Decides when an open position must be closed. Deterministic risk code,
/// not strategy: v3's rule is that hard exits must not depend on an LLM, and
/// the strategy does not get to decide whether a stop applies.
/// </summary>
public static class ExitManager
{
    public static ExitDecision Evaluate(
        PositionSnapshot position,
        ExitPolicy exitPolicy,
        SessionSchedule schedule,
        DateTimeOffset now)
    {
        if (position.MarketValue <= 0) return ExitDecision.Hold;

        // Checked first so no later rule, or a bug in one, can keep the
        // position through the night.
        if (now >= schedule.FlattenUtc)
        {
            var remaining = schedule.CloseUtc - now;
            return new ExitDecision(true, ExitReason.SessionClose,
                remaining <= TimeSpan.Zero
                    ? "The session has closed; day-trading positions are not carried overnight."
                    : $"{remaining.TotalMinutes:0} minutes to the close; flattening for the day.");
        }

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

        if (position.OpenedAtUtc is { } openedAt && now - openedAt >= exitPolicy.MaxHoldTime)
            return new ExitDecision(true, ExitReason.MaxHoldTime,
                $"Held {(now - openedAt).TotalMinutes:0} minutes without reaching the stop or the target; the setup has expired.");

        return ExitDecision.Hold;
    }
}
