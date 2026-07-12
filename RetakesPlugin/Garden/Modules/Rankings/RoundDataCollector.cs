using GardenRankingsCore.Config;
using GardenRankingsCore.Models;

namespace GardenRankings;

/// <summary>
/// Collects everything that happens during one live round into PlayerRoundStats
/// sheets: kills, assists, damage, utility, flashes, opening duels, trades, KAST,
/// clutches and objectives. The plugin feeds it raw game events; it knows nothing
/// about CounterStrikeSharp types so it stays unit-testable.
/// </summary>
public class RoundDataCollector
{
    private record KillEvent(ulong KillerId, ulong VictimId, int VictimTeamNum, double AtSeconds);

    public bool IsCollecting { get; private set; }
    public RoundContext Context { get; private set; } = new();

    private readonly Dictionary<ulong, PlayerRoundStats> _stats = new();
    private readonly HashSet<ulong> _alive = new();
    private readonly List<KillEvent> _killEvents = new();
    private readonly HashSet<int> _clutchRecordedTeams = new();
    private bool _firstKillDone;
    private DateTime _startedAtUtc;

    public IReadOnlyDictionary<ulong, PlayerRoundStats> Stats => _stats;

    public void BeginRound(
        string map,
        RetakesRoundType roundType,
        bool isRanked,
        ICollection<(ulong SteamId, string Name, int TeamNum, int Elo)> players
    )
    {
        _stats.Clear();
        _alive.Clear();
        _killEvents.Clear();
        _clutchRecordedTeams.Clear();
        _firstKillDone = false;
        _startedAtUtc = DateTime.UtcNow;

        foreach (var (steamId, name, teamNum, elo) in players)
        {
            if (steamId == 0 || teamNum is not (TeamNums.T or TeamNums.Ct))
            {
                continue;
            }

            _stats[steamId] = new PlayerRoundStats
            {
                SteamId = steamId,
                PlayerName = name,
                TeamNum = teamNum,
                EloBefore = elo,
            };
            _alive.Add(steamId);
        }

        Context = new RoundContext
        {
            Map = map,
            StartedAtUtc = _startedAtUtc,
            RoundType = roundType,
            IsRanked = isRanked,
            TPlayerCount = _stats.Values.Count(s => s.TeamNum == TeamNums.T),
            CtPlayerCount = _stats.Values.Count(s => s.TeamNum == TeamNums.Ct),
        };

        IsCollecting = Context.TPlayerCount > 0 && Context.CtPlayerCount > 0;
    }

    private double SecondsSinceStart => (DateTime.UtcNow - _startedAtUtc).TotalSeconds;

    private PlayerRoundStats? Get(ulong steamId)
    {
        return _stats.TryGetValue(steamId, out var s) ? s : null;
    }

    public void OnPlayerDeath(ulong victimId, ulong killerId, ulong assisterId, bool headshot, bool assistedFlash)
    {
        if (!IsCollecting)
        {
            return;
        }

        var victim = Get(victimId);
        if (victim is null)
        {
            return;
        }

        var now = SecondsSinceStart;
        victim.Died = true;
        victim.DiedAtSeconds = now;
        _alive.Remove(victimId);

        var killer = killerId != 0 && killerId != victimId ? Get(killerId) : null;

        if (killer is not null && killer.TeamNum == victim.TeamNum)
        {
            // Teamkill: no credit, mark both sides.
            victim.WasTeamKilled = true;
            killer.KilledTeammate = true;
        }
        else if (killer is not null)
        {
            killer.Kills++;
            if (headshot)
            {
                killer.Headshots++;
            }

            if (!_firstKillDone)
            {
                _firstKillDone = true;
                killer.OpeningKill = true;
                victim.OpeningDeath = true;
            }

            // Trade detection: the victim recently killed one of the killer's teammates.
            var window = Configs.GetConfigData().TradeWindowSeconds;
            var traded = false;
            foreach (var e in _killEvents)
            {
                if (e.KillerId == victimId &&
                    now - e.AtSeconds <= window &&
                    e.VictimTeamNum == killer.TeamNum)
                {
                    var tradedVictim = Get(e.VictimId);
                    if (tradedVictim is not null)
                    {
                        tradedVictim.TradedDeath = true;
                        traded = true;
                    }
                }
            }

            if (traded)
            {
                killer.TradeKills++;
            }

            _killEvents.Add(new KillEvent(killerId, victimId, victim.TeamNum, now));
        }

        if (assisterId != 0)
        {
            var assister = Get(assisterId);
            if (assister is not null && assister.TeamNum != victim.TeamNum)
            {
                if (assistedFlash)
                {
                    assister.FlashAssists++;
                }
                else
                {
                    assister.Assists++;
                }
            }
        }

        DetectClutchSituations();
    }

    private void DetectClutchSituations()
    {
        foreach (var teamNum in new[] {TeamNums.T, TeamNums.Ct})
        {
            if (_clutchRecordedTeams.Contains(teamNum))
            {
                continue;
            }

            var aliveTeam = _stats.Values.Where(s => s.TeamNum == teamNum && _alive.Contains(s.SteamId)).ToList();
            var aliveEnemies = _stats.Values.Count(s => s.TeamNum != teamNum && _alive.Contains(s.SteamId));

            if (aliveTeam.Count == 1 && aliveEnemies >= 1)
            {
                aliveTeam[0].ClutchVersus = aliveEnemies;
                _clutchRecordedTeams.Add(teamNum);
            }
        }
    }

    public void OnPlayerHurt(ulong victimId, ulong attackerId, int damage, string weapon)
    {
        if (!IsCollecting || attackerId == 0 || attackerId == victimId)
        {
            return;
        }

        var attacker = Get(attackerId);
        var victim = Get(victimId);
        if (attacker is null || victim is null || attacker.TeamNum == victim.TeamNum)
        {
            return;
        }

        attacker.Damage += Math.Max(0, damage);

        if (IsUtilityWeapon(weapon))
        {
            attacker.UtilityDamage += Math.Max(0, damage);
        }
    }

    private static bool IsUtilityWeapon(string weapon)
    {
        return weapon is "hegrenade" or "molotov" or "inferno" or "incgrenade"
            or "weapon_hegrenade" or "weapon_molotov" or "weapon_incgrenade";
    }

    public void OnPlayerBlind(ulong victimId, ulong attackerId, double duration)
    {
        if (!IsCollecting || attackerId == 0 || attackerId == victimId)
        {
            return;
        }

        if (duration < Configs.GetConfigData().MinBlindDurationSeconds)
        {
            return;
        }

        var attacker = Get(attackerId);
        var victim = Get(victimId);
        if (attacker is null || victim is null || attacker.TeamNum == victim.TeamNum)
        {
            return;
        }

        attacker.EnemiesFlashed++;
        attacker.EnemyBlindDuration += duration;
    }

    public void OnBombPlanted(ulong playerId, string? site)
    {
        if (!IsCollecting)
        {
            return;
        }

        Context.BombPlanted = true;
        Context.BombSite = site;

        var player = Get(playerId);
        if (player is not null)
        {
            player.Planted = true;
        }
    }

    public void OnBombDefused(ulong playerId)
    {
        if (!IsCollecting)
        {
            return;
        }

        Context.BombDefused = true;

        var player = Get(playerId);
        if (player is not null)
        {
            player.Defused = true;
        }
    }

    public void OnBombExploded()
    {
        if (IsCollecting)
        {
            Context.BombExploded = true;
        }
    }

    public void MarkAfk(ulong steamId)
    {
        var player = Get(steamId);
        if (player is not null)
        {
            player.WasAfk = true;
        }
    }

    /// <summary>
    /// Finalizes the round. Returns the context and the completed stat sheets
    /// (ratings and ELO still need to be computed by the engines).
    /// </summary>
    public (RoundContext Context, List<PlayerRoundStats> Players) EndRound(int winnerTeamNum)
    {
        IsCollecting = false;

        Context.WinnerTeamNum = winnerTeamNum;
        Context.RoundDurationSeconds = SecondsSinceStart;

        foreach (var s in _stats.Values)
        {
            s.WonRound = s.TeamNum == winnerTeamNum;
            if (s.ClutchVersus > 0 && s.WonRound)
            {
                s.ClutchWon = true;
            }
        }

        return (Context, _stats.Values.ToList());
    }

    public void Abort()
    {
        IsCollecting = false;
        _stats.Clear();
        _alive.Clear();
    }
}
