namespace GardenRetakes.Core.GameModes;

/// <summary>
/// The exclusive game modes of Garden Retakes. Exactly one is active at a time.
/// SmallServer is NOT a mode — it is an overlay on top of Retakes (see
/// <see cref="SmallServerOverlay"/>).
/// </summary>
public enum GameModeKind
{
    Retakes,
    Duels,
    Executes,
    FastStrat,

    /// <summary>R11: dedicated editing mode — no bomb, no timer, noclip, markers.</summary>
    Edit,
}

public enum SmallServerState
{
    Auto,
    ForcedOn,
    ForcedOff,
}

/// <summary>
/// Pure-logic mode state machine (no CounterStrikeSharp dependency — unit tested).
/// The plugin layer subscribes to <see cref="ModeChanged"/> to apply cvar
/// profiles, spawn sets and round flow of the new mode.
/// </summary>
public class GameModeManager
{
    public GameModeKind CurrentMode { get; private set; } = GameModeKind.Retakes;

    /// <summary>
    /// Set by the plugin while a match/vote that must not be interrupted is
    /// running (CR match, active duel bracket, ...). Blocks mode changes.
    /// </summary>
    public bool IsMatchInProgress { get; set; }

    public SmallServerState SmallServer { get; private set; } = SmallServerState.Auto;

    /// <summary>Humans ≤ this (and > 0) activate the small-server overlay in Auto.</summary>
    public int SmallServerMaxHumans { get; set; } = 3;

    public event Action<GameModeKind, GameModeKind>? ModeChanged;

    public bool TryChangeMode(GameModeKind target, out string? error)
    {
        error = null;
        if (target == CurrentMode)
        {
            error = "already_active";
            return false;
        }

        if (IsMatchInProgress)
        {
            error = "match_in_progress";
            return false;
        }

        var previous = CurrentMode;
        CurrentMode = target;
        ModeChanged?.Invoke(previous, target);
        return true;
    }

    public static bool TryParseMode(string input, out GameModeKind mode)
    {
        switch (input.Trim().ToLowerInvariant())
        {
            case "retakes" or "retake" or "rt":
                mode = GameModeKind.Retakes;
                return true;
            case "duels" or "duel" or "1v1":
                mode = GameModeKind.Duels;
                return true;
            case "executes" or "execute" or "exec":
                mode = GameModeKind.Executes;
                return true;
            case "faststrat" or "fast-strat" or "fs" or "strat":
                mode = GameModeKind.FastStrat;
                return true;
            case "edit" or "editor" or "editmode":
                mode = GameModeKind.Edit;
                return true;
            default:
                mode = GameModeKind.Retakes;
                return false;
        }
    }

    public void SetSmallServerState(SmallServerState state) => SmallServer = state;

    /// <summary>
    /// Whether the small-server overlay is active for the given human count.
    /// Only meaningful while <see cref="CurrentMode"/> is Retakes.
    /// </summary>
    public bool IsSmallServerActive(int humanCount)
    {
        if (CurrentMode != GameModeKind.Retakes)
        {
            return false;
        }

        return SmallServer switch
        {
            SmallServerState.ForcedOn => true,
            SmallServerState.ForcedOff => false,
            _ => humanCount > 0 && humanCount <= SmallServerMaxHumans,
        };
    }
}
