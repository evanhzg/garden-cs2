using GardenRankingsCore.Config;
using GardenRankingsCore.Models;

namespace GardenRankingsCore.Managers;

public enum CrMatchEvent
{
    None,
    HalftimeReached,
    MatchWonByA,
    MatchWonByB,
    MatchDraw,
}

public record CrRoundPlan(int RoundTypeOrdinal, int? ForceBuyTeamNum, bool IsPistol);

/// <summary>
/// Competitive Retakes (CR) match state: two fixed rosters ("A" started as the Ts,
/// "B" as the CTs) playing MR12 over two halves with a side swap, pistol round to
/// open each half, full buys otherwise, and a force-buy for any team on a losing
/// streak. Pure logic - the plugin layer moves players and talks to the allocator.
/// </summary>
public class CompetitiveMatchManager
{
    public bool IsLive { get; private set; }

    public HashSet<ulong> TeamA { get; } = new();
    public HashSet<ulong> TeamB { get; } = new();
    public string TeamAName { get; private set; } = "";
    public string TeamBName { get; private set; } = "";

    /// <summary>The side (engine team number) Team A is currently playing.</summary>
    public int TeamASideTeamNum { get; private set; }

    public int TeamBSideTeamNum => TeamASideTeamNum == TeamNums.T ? TeamNums.Ct : TeamNums.T;

    public int ScoreA { get; private set; }
    public int ScoreB { get; private set; }
    public int RoundsPlayed { get; private set; }
    public int CurrentHalf { get; private set; } = 1;

    private int _consecutiveLossesA;
    private int _consecutiveLossesB;

    private static CompetitiveConfig Cfg => Configs.GetConfigData().Competitive;

    public static string BuildTeamKey(IEnumerable<ulong> steamIds)
    {
        return string.Join("-", steamIds.OrderBy(id => id));
    }

    public string TeamAKey => BuildTeamKey(TeamA);
    public string TeamBKey => BuildTeamKey(TeamB);

    /// <summary>
    /// Locks the current sides as the two rosters. Team A = the Ts, Team B = the CTs.
    /// </summary>
    public void StartMatch(
        ICollection<ulong> tPlayers,
        ICollection<ulong> ctPlayers,
        string teamAName,
        string teamBName
    )
    {
        TeamA.Clear();
        TeamB.Clear();
        foreach (var id in tPlayers)
        {
            TeamA.Add(id);
        }

        foreach (var id in ctPlayers)
        {
            TeamB.Add(id);
        }

        TeamAName = teamAName;
        TeamBName = teamBName;
        TeamASideTeamNum = TeamNums.T;
        ScoreA = 0;
        ScoreB = 0;
        RoundsPlayed = 0;
        CurrentHalf = 1;
        _consecutiveLossesA = 0;
        _consecutiveLossesB = 0;
        IsLive = true;
    }

    public void Cancel()
    {
        IsLive = false;
        TeamA.Clear();
        TeamB.Clear();
    }

    /// <summary>Which roster a player belongs to ("A"/"B"), or null.</summary>
    public string? RosterOf(ulong steamId)
    {
        if (TeamA.Contains(steamId))
        {
            return "A";
        }

        return TeamB.Contains(steamId) ? "B" : null;
    }

    /// <summary>
    /// The round plan for the upcoming round: pistol to open each half, force-buy
    /// for a team on a losing streak, full buy otherwise.
    /// </summary>
    public CrRoundPlan PlanNextRound()
    {
        var roundsIntoHalf = RoundsPlayed % Cfg.RoundsPerHalf;
        var isFirstOfHalf = roundsIntoHalf == 0;

        if (isFirstOfHalf)
        {
            return new CrRoundPlan((int) RetakesRoundType.Pistol, null, true);
        }

        var threshold = Cfg.ForceBuyAfterConsecutiveLosses;
        if (_consecutiveLossesA >= threshold)
        {
            return new CrRoundPlan((int) RetakesRoundType.ForceBuy, TeamASideTeamNum, false);
        }

        if (_consecutiveLossesB >= threshold)
        {
            return new CrRoundPlan((int) RetakesRoundType.ForceBuy, TeamBSideTeamNum, false);
        }

        return new CrRoundPlan((int) RetakesRoundType.FullBuy, null, false);
    }

    /// <summary>
    /// Registers a finished round. Returns what (if anything) just happened:
    /// halftime (caller must swap the players' sides) or a match result.
    /// </summary>
    public CrMatchEvent RoundCompleted(int winnerTeamNum)
    {
        if (!IsLive)
        {
            return CrMatchEvent.None;
        }

        var teamAWon = winnerTeamNum == TeamASideTeamNum;
        if (teamAWon)
        {
            ScoreA++;
            _consecutiveLossesA = 0;
            _consecutiveLossesB++;
        }
        else
        {
            ScoreB++;
            _consecutiveLossesB = 0;
            _consecutiveLossesA++;
        }

        RoundsPlayed++;

        var roundsToWin = Cfg.RoundsPerHalf + 1;
        if (ScoreA >= roundsToWin)
        {
            IsLive = false;
            return CrMatchEvent.MatchWonByA;
        }

        if (ScoreB >= roundsToWin)
        {
            IsLive = false;
            return CrMatchEvent.MatchWonByB;
        }

        if (RoundsPlayed >= Cfg.RoundsPerHalf * 2)
        {
            IsLive = false;
            return CrMatchEvent.MatchDraw;
        }

        if (RoundsPlayed == Cfg.RoundsPerHalf)
        {
            // Halftime: swap sides, reset streaks, second half opens with a pistol round.
            TeamASideTeamNum = TeamBSideTeamNum;
            CurrentHalf = 2;
            _consecutiveLossesA = 0;
            _consecutiveLossesB = 0;
            return CrMatchEvent.HalftimeReached;
        }

        return CrMatchEvent.None;
    }

    public string ScoreLine()
    {
        return $"{TeamAName} {ScoreA} - {ScoreB} {TeamBName}";
    }
}
