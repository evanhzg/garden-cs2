using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using GardenRetakes.Core.GameModes;
using RetakesPlugin.Utils;

namespace RetakesPlugin.Garden.Modules;

/// <summary>
/// Fast-strat mode (ROADMAP R7): every round, CTs vote a defensive SETUP and Ts
/// vote a STRATEGY — both teams spawn in the chosen situation facing each other,
/// with the strat's utility auto-thrown. Perfect for drilling one situation.
///
/// Data is shared with Executes (same executes/&lt;map&gt;.json — a strategy's
/// CtSetups feed the CT vote, its TStarts + Utilities feed the T vote), so
/// everything created with !gexec is votable here.
///
/// Player commands (any time; tallied per current team at round start):
///   !strat &lt;name&gt; / !strat list    — T-side vote
///   !setup &lt;name&gt; / !setup list    — CT-side vote
/// Unvoted sides get a random playable strategy. Majority wins, ties random.
/// </summary>
public class FastStratModule : IGardenModule
{
    private readonly RetakesPlugin _plugin;
    private readonly GardenHost _host;
    private readonly ExecutesModule _executes;
    private readonly Random _random = new();

    // steamid -> voted strategy name, per side.
    private readonly Dictionary<ulong, string> _tVotes = new();
    private readonly Dictionary<ulong, string> _ctVotes = new();

    private ExecuteStrategy? _currentT;
    private ExecuteStrategy? _currentCt;

    public string Name => "FastStrat";
    public bool Enabled => _host.Settings.FastStrat.Enabled;

    public FastStratModule(RetakesPlugin plugin, GardenHost host, ExecutesModule executes)
    {
        _plugin = plugin;
        _host = host;
        _executes = executes;
    }

    private bool IsActive => _host.Modes.CurrentMode == GameModeKind.FastStrat;

    private string Prefix => _plugin.Localizer["garden.prefix"];

    public void Load(bool hotReload)
    {
        _host.Modes.ModeChanged += OnModeChanged;

        _plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        _plugin.RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);

        _plugin.AddCommand("css_strat", "Fast-strat: vote the T strategy. Usage: !strat <name|list>", OnStratCommand);
        _plugin.AddCommand("css_setup", "Fast-strat: vote the CT setup. Usage: !setup <name|list>", OnSetupCommand);
    }

    public void OnMapStart(string mapName)
    {
        _tVotes.Clear();
        _ctVotes.Clear();
        _currentT = null;
        _currentCt = null;
    }

    public void Unload()
    {
        _host.Modes.ModeChanged -= OnModeChanged;
    }

    // ---------- mode lifecycle ----------

    private void OnModeChanged(GameModeKind from, GameModeKind to)
    {
        if (to == GameModeKind.FastStrat)
        {
            if (_executes.Store.Playable.Count == 0)
            {
                Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.fs.no_strategies"]}");
                _host.Modes.TryChangeMode(GameModeKind.Retakes, out _);
                return;
            }

            _tVotes.Clear();
            _ctVotes.Clear();
            foreach (var command in _host.Settings.Executes.StartCommands)
            {
                Server.ExecuteCommand(command);
            }

            Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.fs.started",
                _executes.Store.Playable.Count]}");
        }
        else if (from == GameModeKind.FastStrat)
        {
            _currentT = null;
            _currentCt = null;
            foreach (var command in _host.Settings.Executes.StopCommands)
            {
                Server.ExecuteCommand(command);
            }

            Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.fs.stopped"]}");
        }
    }

    // ---------- voting ----------

    private void OnStratCommand(CCSPlayerController? player, CommandInfo info) =>
        HandleVote(player, info, CsTeam.Terrorist, _tVotes);

    private void OnSetupCommand(CCSPlayerController? player, CommandInfo info) =>
        HandleVote(player, info, CsTeam.CounterTerrorist, _ctVotes);

    private void HandleVote(CCSPlayerController? player, CommandInfo info,
        CsTeam side, Dictionary<ulong, string> votes)
    {
        if (!IsActive)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.fs.not_active"]}");
            return;
        }

        if (!PlayerHelper.IsValid(player))
        {
            return;
        }

        var arg = info.GetArg(1);
        if (string.IsNullOrWhiteSpace(arg) || arg.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.fs.list_header"]}");
            foreach (var strategy in _executes.Store.Playable)
            {
                info.ReplyToCommand($"{Prefix} {strategy.Name} @{strategy.Site}");
            }

            return;
        }

        if (player!.Team != side)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.fs.wrong_side",
                side == CsTeam.Terrorist ? "T" : "CT"]}");
            return;
        }

        var voted = _executes.Store.Find(arg);
        if (voted is null || !voted.IsPlayable)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.fs.unknown", arg]}");
            return;
        }

        votes[player.SteamID] = voted.Name;
        info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.fs.vote_registered",
            voted.Name]}");
    }

    private ExecuteStrategy? TallyVotes(Dictionary<ulong, string> votes)
    {
        if (votes.Count == 0)
        {
            return _executes.Store.PickRandom(_random);
        }

        var top = votes.Values
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();

        var best = top[0].Count();
        var tied = top.Where(g => g.Count() == best).Select(g => g.Key).ToList();
        var winner = tied[_random.Next(tied.Count)];

        return _executes.Store.Find(winner) ?? _executes.Store.PickRandom(_random);
    }

    // ---------- round flow ----------

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (!IsActive)
        {
            return HookResult.Continue;
        }

        _currentT = TallyVotes(_tVotes);
        _currentCt = TallyVotes(_ctVotes);
        _tVotes.Clear();
        _ctVotes.Clear();

        if (_currentT is null || _currentCt is null)
        {
            Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.fs.no_strategies"]}");
            return HookResult.Continue;
        }

        _plugin.AddTimer(0.3f, SetupRound);
        return HookResult.Continue;
    }

    private void SetupRound()
    {
        if (!IsActive || _currentT is null || _currentCt is null)
        {
            return;
        }

        var cfg = _host.Settings.Executes;
        var ts = Utilities.GetPlayers()
            .Where(p => PlayerHelper.IsValid(p) && p.Team == CsTeam.Terrorist && p.PawnIsAlive)
            .ToList();
        var cts = Utilities.GetPlayers()
            .Where(p => PlayerHelper.IsValid(p) && p.Team == CsTeam.CounterTerrorist && p.PawnIsAlive)
            .ToList();

        _executes.PlaceGroup(ts, _currentT.TStarts, cfg.TWeapons, cfg.GiveKevlarHelmet);
        _executes.PlaceGroup(cts, _currentCt.CtSetups, cfg.CtWeapons, cfg.GiveKevlarHelmet);
        var bombCarrier = ExecutesModule.PickBombCarrier(ts);
        bombCarrier?.GiveNamedItem("weapon_c4");

        Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.fs.playing",
            _currentT.Name, _currentCt.Name]}");
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        if (!IsActive)
        {
            return HookResult.Continue;
        }

        // R10: each side replays ITS OWN lineups — the T strat's T utility and
        // the CT setup's CT utility.
        var throws = new List<UtilityThrow>();
        if (_currentT is not null)
        {
            throws.AddRange(_currentT.Utilities.Where(u =>
                !u.Team.Equals("CT", StringComparison.OrdinalIgnoreCase)));
        }

        if (_currentCt is not null)
        {
            throws.AddRange(_currentCt.Utilities.Where(u =>
                u.Team.Equals("CT", StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var utility in throws)
        {
            var toThrow = utility;
            _plugin.AddTimer(Math.Max(0.1f, toThrow.DelaySeconds), () => _executes.ThrowUtility(toThrow));
        }

        return HookResult.Continue;
    }
}
