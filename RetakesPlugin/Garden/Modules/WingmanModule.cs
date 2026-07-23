using CounterStrikeSharp.API;
using GardenRetakes.Core.GameModes;
using RetakesPlugin.Utils;

namespace RetakesPlugin.Garden.Modules;

public class WingmanModule : IGardenModule
{
    private readonly RetakesPlugin _plugin;
    private readonly GardenHost _host;

    public string Name => "Wingman";
    public bool Enabled => _host.Settings.Wingman.Enabled;

    public WingmanModule(RetakesPlugin plugin, GardenHost host)
    {
        _plugin = plugin;
        _host = host;
    }

    public void Load(bool hotReload)
    {
        _host.Modes.ModeChanged += OnModeChanged;
    }

    public void OnMapStart(string mapName)
    {
        if (_host.Modes.CurrentMode == GameModeKind.Wingman)
        {
            ApplyStartCommands();
        }
    }

    public void Unload()
    {
        _host.Modes.ModeChanged -= OnModeChanged;
    }

    private void OnModeChanged(GameModeKind oldMode, GameModeKind newMode)
    {
        if (oldMode == GameModeKind.Wingman)
        {
            ApplyStopCommands();
        }

        if (newMode == GameModeKind.Wingman)
        {
            ApplyStartCommands();
        }
    }

    private void ApplyStartCommands()
    {
        if (!Enabled) return;

        foreach (var cmd in _host.Settings.Wingman.StartCommands)
        {
            Server.ExecuteCommand(cmd);
        }
    }

    private void ApplyStopCommands()
    {
        foreach (var cmd in _host.Settings.Wingman.StopCommands)
        {
            Server.ExecuteCommand(cmd);
        }
    }
}
