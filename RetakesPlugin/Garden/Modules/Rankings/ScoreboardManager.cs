using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using GardenRankingsCore.Config;

namespace GardenRankings;

/// <summary>
/// Displays each player's ELO as a Premier-style "CS Rating" number on the scoreboard
/// by writing the competitive ranking fields on the player controller.
///
/// Technique verified against K4-System-MMRanks (GPL): the fields must be re-applied
/// continuously (the game resets them), CompetitiveWins must be at least 10 for the
/// rating number to render (fewer wins shows a placement state), and rank type 11 is
/// the Premier "CS Rating" display. To reveal OTHER players' ratings on the scoreboard
/// (instead of only your own), install the companion Metamod plugin
/// https://github.com/Cruze03/FakeRanks-RevealAll - without it each client only sees
/// their own rating until the match-end reveal.
/// </summary>
public static class ScoreboardManager
{
    // Premier / CS Rating rank type.
    private const sbyte PremierRankType = 11;

    public static void SetPlayerScoreboardElo(CCSPlayerController player, int elo, int rankedWins)
    {
        if (!Configs.GetConfigData().EnableScoreboardRanks || !Helpers.PlayerIsValid(player) || player.IsBot)
        {
            return;
        }

        player.CompetitiveRankType = PremierRankType;
        player.CompetitiveRanking = elo;
        // The scoreboard only renders the CS Rating number once the player has
        // "enough" wins; below that it renders a placement progress instead.
        player.CompetitiveWins = Math.Max(10, rankedWins);

        Utilities.SetStateChanged(player, "CCSPlayerController", "m_iCompetitiveRankType");
    }
}
