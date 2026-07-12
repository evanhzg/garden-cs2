using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

using RetakesPlugin.Models;
using RetakesPlugin.Services;
using RetakesPlugin.Utils;
using RetakesPluginShared.Enums;

namespace RetakesPlugin.Managers;

public class SpawnManager
{
    /// <summary>
    /// Garden (ROADMAP R5): optional per-round spawn filter, e.g. the small-server
    /// overlay narrowing to "smallserver"-flagged spawns. Returning the input list
    /// means "no filtering". Only applied when the filtered set still has enough
    /// spawns (and a planter spawn for T) — otherwise the full set is used.
    /// </summary>
    public static Func<List<Spawn>, CsTeam, List<Spawn>>? GardenSpawnFilter { get; set; }

    private readonly RetakesPlugin _plugin;
    private readonly MapConfigService _mapConfigService;
    private readonly Dictionary<Bombsite, Dictionary<CsTeam, List<Spawn>>> _spawns = new();
    private readonly Random _random = new();

    public SpawnManager(RetakesPlugin plugin, MapConfigService mapConfigService)
    {
        _plugin = plugin;
        _mapConfigService = mapConfigService;
        CalculateMapSpawns();
    }

    public void CalculateMapSpawns()
    {
        _spawns.Clear();

        _spawns.Add(Bombsite.A, new Dictionary<CsTeam, List<Spawn>>()
        {
            { CsTeam.Terrorist, [] },
            { CsTeam.CounterTerrorist, [] }
        });
        _spawns.Add(Bombsite.B, new Dictionary<CsTeam, List<Spawn>>()
        {
            { CsTeam.Terrorist, [] },
            { CsTeam.CounterTerrorist, [] }
        });

        foreach (var spawn in _mapConfigService.GetSpawnsClone())
        {
            _spawns[spawn.Bombsite][spawn.Team].Add(spawn);
        }

        Logger.LogInfo("SpawnManager", "Map spawns calculated successfully");
    }

    public List<Spawn> GetSpawns(Bombsite bombsite, CsTeam? team = null)
    {
        if (_spawns[bombsite][CsTeam.Terrorist].Count == 0 &&
            _spawns[bombsite][CsTeam.CounterTerrorist].Count == 0)
        {
            Logger.LogWarning("SpawnManager", $"No spawns found for bombsite {bombsite}");
            return [];
        }

        if (team == null)
        {
            return _spawns[bombsite].SelectMany(entry => entry.Value).ToList();
        }

        return _spawns[bombsite][(CsTeam)team];
    }

    public CCSPlayerController? HandleRoundSpawns(Bombsite bombsite, HashSet<CCSPlayerController> players)
    {
        Logger.LogDebug("SpawnManager", $"Handling round spawns for bombsite {bombsite}");

        var spawns = _spawns[bombsite].ToDictionary(
            entry => entry.Key,
            entry => entry.Value.ToList()
        );

        var ctCount = PlayerHelper.GetPlayerCount(CsTeam.CounterTerrorist);
        var tCount = PlayerHelper.GetPlayerCount(CsTeam.Terrorist);

        // Garden (R5): apply the optional spawn filter when it leaves enough spawns.
        if (GardenSpawnFilter is not null)
        {
            var filteredT = GardenSpawnFilter(spawns[CsTeam.Terrorist], CsTeam.Terrorist);
            var filteredCt = GardenSpawnFilter(spawns[CsTeam.CounterTerrorist], CsTeam.CounterTerrorist);
            if (filteredT.Count >= tCount && filteredCt.Count >= ctCount &&
                filteredT.Any(spawn => spawn.CanBePlanter))
            {
                spawns[CsTeam.Terrorist] = filteredT;
                spawns[CsTeam.CounterTerrorist] = filteredCt;
                Logger.LogDebug("SpawnManager",
                    $"Garden spawn filter active: T {filteredT.Count}, CT {filteredCt.Count}");
            }
        }

        // Garden (R13): Scenario groups
        if (_plugin.ScenariosEnabled.Value)
        {
            var scenarios = spawns[CsTeam.Terrorist]
                .SelectMany(s => s.Flags)
                .Where(f => f.StartsWith("scenario:", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();

            // Intersect with CT scenarios to ensure the scenario is valid for both teams
            var ctScenarios = spawns[CsTeam.CounterTerrorist]
                .SelectMany(s => s.Flags)
                .Where(f => f.StartsWith("scenario:", StringComparison.OrdinalIgnoreCase))
                .Distinct();
            
            var validScenarios = scenarios.Intersect(ctScenarios).ToList();

            if (validScenarios.Count > 0)
            {
                var chosenScenario = validScenarios[_random.Next(validScenarios.Count)];
                var scenarioT = spawns[CsTeam.Terrorist].Where(s => s.Flags.Contains(chosenScenario, StringComparer.OrdinalIgnoreCase)).ToList();
                var scenarioCt = spawns[CsTeam.CounterTerrorist].Where(s => s.Flags.Contains(chosenScenario, StringComparer.OrdinalIgnoreCase)).ToList();

                if (scenarioT.Count >= tCount && scenarioCt.Count >= ctCount && scenarioT.Any(s => s.CanBePlanter))
                {
                    spawns[CsTeam.Terrorist] = scenarioT;
                    spawns[CsTeam.CounterTerrorist] = scenarioCt;
                    Logger.LogInfo("SpawnManager", $"Scenario {chosenScenario} selected for bombsite {bombsite}");
                }
                else
                {
                    Logger.LogWarning("SpawnManager", $"Scenario {chosenScenario} does not have enough valid spawns for {tCount} T / {ctCount} CT. Falling back.");
                }
            }
        }

        if (ctCount > spawns[CsTeam.CounterTerrorist].Count ||
            tCount > spawns[CsTeam.Terrorist].Count)
        {
            Logger.LogError("SpawnManager",
                $"Not enough spawns for bombsite {bombsite}! CT: {ctCount}/{spawns[CsTeam.CounterTerrorist].Count}, T: {tCount}/{spawns[CsTeam.Terrorist].Count}");
            throw new Exception($"Not enough spawns in map config for Bombsite {bombsite}!");
        }

        var planterSpawns = spawns[CsTeam.Terrorist].Where(spawn => spawn.CanBePlanter).ToList();

        if (planterSpawns.Count == 0)
        {
            Logger.LogError("SpawnManager", $"No planter spawns found for bombsite {bombsite}");
            throw new Exception($"No planter spawns for Bombsite {bombsite}!");
        }

        var randomPlanterSpawn = planterSpawns[_random.Next(planterSpawns.Count)];
        spawns[CsTeam.Terrorist].Remove(randomPlanterSpawn);

        CCSPlayerController? planter = null;

        foreach (var player in PlayerHelper.Shuffle(players, _random))
        {
            if (!PlayerHelper.HasAlivePawn(player))
            {
                continue;
            }

            var team = player.Team;
            if (team != CsTeam.Terrorist && team != CsTeam.CounterTerrorist)
            {
                continue;
            }

            if (planter == null && team == CsTeam.Terrorist)
            {
                planter = player;
            }

            var count = spawns[team].Count;
            if (count == 0)
            {
                continue;
            }

            var spawn = player == planter ? randomPlanterSpawn : spawns[team][_random.Next(count)];

            player.Pawn.Value!.Teleport(spawn.Vector, spawn.QAngle, new Vector());
            spawns[team].Remove(spawn);
        }

        Logger.LogInfo("SpawnManager", $"Players moved to spawns. Planter: {planter?.PlayerName ?? "None"}");

        return planter;
    }
}