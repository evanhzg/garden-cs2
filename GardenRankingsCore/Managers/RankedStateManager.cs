using GardenRankingsCore.Config;

namespace GardenRankingsCore.Managers;

public enum RankedState
{
    Inactive,
    StartVoteInProgress,
    Active,
    StopConfirmPending,
    StopVoteInProgress,
}

public enum StartVoteOutcome
{
    VoteStarted,
    AlreadyActive,
    VoteAlreadyInProgress,
    NotEnoughPlayers,
}

public enum VoteCastResult
{
    NoVoteInProgress,
    NotEligible,
    AlreadyVoted,
    Registered,
    Passed,
    Failed,
}

public enum TickOutcome
{
    None,
    StartVotePassed,
    StartVoteFailed,
    StopVotePassed,
    StopVoteFailed,
    StopConfirmExpired,
}

public enum StopRequestOutcome
{
    NotActive,
    ConfirmationPending,
    StopVoteStarted,
    VoteAlreadyInProgress,
}

/// <summary>
/// Pure state machine for the Ranked Retakes session lifecycle:
/// Inactive -> (vote) -> Active -> (confirm/vote) -> Inactive.
/// Time is injected so the whole flow is unit-testable. The plugin layer is
/// responsible for chat prompts and for calling Tick() periodically.
/// </summary>
public class RankedStateManager
{
    public RankedState State { get; private set; } = RankedState.Inactive;
    public bool IsActive => State is RankedState.Active or RankedState.StopConfirmPending
        or RankedState.StopVoteInProgress;

    public ulong VoteInitiator { get; private set; }
    public DateTime VoteDeadlineUtc { get; private set; }

    private readonly HashSet<ulong> _eligibleVoters = new();
    private readonly HashSet<ulong> _accepted = new();
    private readonly HashSet<ulong> _declined = new();

    public IReadOnlyCollection<ulong> EligibleVoters => _eligibleVoters;
    public IReadOnlyCollection<ulong> Accepted => _accepted;

    public IEnumerable<ulong> PendingVoters =>
        _eligibleVoters.Where(v => !_accepted.Contains(v) && !_declined.Contains(v));

    private static RankedConfig Cfg => Configs.GetConfigData().Ranked;

    public void Reset()
    {
        State = RankedState.Inactive;
        ClearVote();
    }

    private void ClearVote()
    {
        VoteInitiator = 0;
        _eligibleVoters.Clear();
        _accepted.Clear();
        _declined.Clear();
    }

    #region Start flow

    public StartVoteOutcome TryStartVote(ulong initiator, ICollection<ulong> eligiblePlayers, DateTime utcNow)
    {
        if (IsActive)
        {
            return StartVoteOutcome.AlreadyActive;
        }

        if (State == RankedState.StartVoteInProgress)
        {
            return StartVoteOutcome.VoteAlreadyInProgress;
        }

        if (eligiblePlayers.Count < Cfg.MinPlayers)
        {
            return StartVoteOutcome.NotEnoughPlayers;
        }

        ClearVote();
        State = RankedState.StartVoteInProgress;
        VoteInitiator = initiator;
        VoteDeadlineUtc = utcNow.AddSeconds(Cfg.VoteDurationSeconds);
        foreach (var p in eligiblePlayers)
        {
            _eligibleVoters.Add(p);
        }

        // The initiator implicitly accepts.
        _accepted.Add(initiator);

        return StartVoteOutcome.VoteStarted;
    }

    public VoteCastResult CastVote(ulong voter, bool accept, DateTime utcNow)
    {
        if (State is not (RankedState.StartVoteInProgress or RankedState.StopVoteInProgress))
        {
            return VoteCastResult.NoVoteInProgress;
        }

        if (!_eligibleVoters.Contains(voter))
        {
            return VoteCastResult.NotEligible;
        }

        if (_accepted.Contains(voter) || _declined.Contains(voter))
        {
            return VoteCastResult.AlreadyVoted;
        }

        if (accept)
        {
            _accepted.Add(voter);
        }
        else
        {
            _declined.Add(voter);
        }

        return EvaluateVote(finalize: false);
    }

    private int RequiredAccepts => (int) Math.Ceiling(Cfg.VoteRequiredRatio * _eligibleVoters.Count);

    private VoteCastResult EvaluateVote(bool finalize)
    {
        var required = RequiredAccepts;

        if (_accepted.Count >= required)
        {
            CompleteVote(passed: true);
            return VoteCastResult.Passed;
        }

        // Fail as soon as the required ratio can no longer be reached.
        var maxPossibleAccepts = _eligibleVoters.Count - _declined.Count;
        if (maxPossibleAccepts < required || finalize)
        {
            CompleteVote(passed: false);
            return VoteCastResult.Failed;
        }

        return VoteCastResult.Registered;
    }

    private void CompleteVote(bool passed)
    {
        var wasStopVote = State == RankedState.StopVoteInProgress;
        ClearVote();
        State = passed
            ? wasStopVote ? RankedState.Inactive : RankedState.Active
            : wasStopVote
                ? RankedState.Active
                : RankedState.Inactive;
    }

    #endregion

    #region Stop flow

    public StopRequestOutcome RequestStop(ulong initiator, ICollection<ulong> eligiblePlayers, DateTime utcNow)
    {
        if (!IsActive)
        {
            return StopRequestOutcome.NotActive;
        }

        if (State is RankedState.StopVoteInProgress)
        {
            return StopRequestOutcome.VoteAlreadyInProgress;
        }

        if (Cfg.StopRequiresVote)
        {
            ClearVote();
            State = RankedState.StopVoteInProgress;
            VoteInitiator = initiator;
            VoteDeadlineUtc = utcNow.AddSeconds(Cfg.VoteDurationSeconds);
            foreach (var p in eligiblePlayers)
            {
                _eligibleVoters.Add(p);
            }

            _accepted.Add(initiator);
            return StopRequestOutcome.StopVoteStarted;
        }

        State = RankedState.StopConfirmPending;
        VoteInitiator = initiator;
        VoteDeadlineUtc = utcNow.AddSeconds(Cfg.StopConfirmSeconds);
        return StopRequestOutcome.ConfirmationPending;
    }

    /// <summary>
    /// The initiator confirms leaving ranked (typing /rr again or /yes).
    /// Returns true when ranked was deactivated.
    /// </summary>
    public bool ConfirmStop(ulong player, DateTime utcNow)
    {
        if (State != RankedState.StopConfirmPending || player != VoteInitiator || utcNow > VoteDeadlineUtc)
        {
            return false;
        }

        State = RankedState.Inactive;
        ClearVote();
        return true;
    }

    public void CancelStopConfirm()
    {
        if (State == RankedState.StopConfirmPending)
        {
            State = RankedState.Active;
            VoteInitiator = 0;
        }
    }

    #endregion

    #region Lifecycle

    public TickOutcome Tick(DateTime utcNow)
    {
        switch (State)
        {
            case RankedState.StartVoteInProgress when utcNow > VoteDeadlineUtc:
            {
                if (Cfg.CountNonVotersAsAccept)
                {
                    foreach (var pending in PendingVoters.ToList())
                    {
                        _accepted.Add(pending);
                    }
                }

                var result = EvaluateVote(finalize: true);
                return result == VoteCastResult.Passed ? TickOutcome.StartVotePassed : TickOutcome.StartVoteFailed;
            }
            case RankedState.StopVoteInProgress when utcNow > VoteDeadlineUtc:
            {
                if (Cfg.CountNonVotersAsAccept)
                {
                    foreach (var pending in PendingVoters.ToList())
                    {
                        _accepted.Add(pending);
                    }
                }

                var result = EvaluateVote(finalize: true);
                return result == VoteCastResult.Passed ? TickOutcome.StopVotePassed : TickOutcome.StopVoteFailed;
            }
            case RankedState.StopConfirmPending when utcNow > VoteDeadlineUtc:
                State = RankedState.Active;
                VoteInitiator = 0;
                return TickOutcome.StopConfirmExpired;
            default:
                return TickOutcome.None;
        }
    }

    /// <summary>
    /// Call whenever the number of eligible (team-joined, human) players changes.
    /// Returns true when ranked was force-stopped because the count fell below the minimum.
    /// </summary>
    public bool OnEligiblePlayerCountChanged(int count)
    {
        if (!IsActive || count >= Cfg.MinPlayers)
        {
            return false;
        }

        State = RankedState.Inactive;
        ClearVote();
        return true;
    }

    public void ForceDeactivate()
    {
        State = RankedState.Inactive;
        ClearVote();
    }

    /// <summary>
    /// Test/admin helper: activates ranked without a vote.
    /// </summary>
    public void ForceActivate()
    {
        State = RankedState.Active;
        ClearVote();
    }

    #endregion
}
