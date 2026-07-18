using GardenRankings;
using GardenRetakes.Core.GameModes;
using RetakesAllocator;
using RetakesPlugin.Garden.Modules;
using RetakesPlugin.Garden.Modules.Rankings;

namespace RetakesPlugin.Garden;

/// <summary>
/// Owns all Garden modules and the shared <see cref="GameModeManager"/>.
/// Constructed once in RetakesPlugin.Load; forwards lifecycle callbacks.
/// </summary>
public class GardenHost
{
    private readonly RetakesPlugin _plugin;
    private readonly List<IGardenModule> _modules = [];

    public GameModeManager Modes { get; } = new();

    public GardenSettings Settings => _plugin.Config.Garden;

    public GardenHost(RetakesPlugin plugin)
    {
        _plugin = plugin;

        Modes.SmallServerMaxHumans = Settings.SmallServer.MaxHumans;
        Modes.SetSmallServerState(Settings.SmallServer.Mode.Trim().ToLowerInvariant() switch
        {
            "on" => SmallServerState.ForcedOn,
            "off" => SmallServerState.ForcedOff,
            _ => SmallServerState.Auto,
        });

        var admin = new AdminModule(plugin, this);

        _modules.Add(admin);
        _modules.Add(new AllocatorModule(plugin, this));
        _modules.Add(new RankingsModule(plugin, this));
        _modules.Add(new ChatTagModule(plugin));
        _modules.Add(new InstantDefuseModule(plugin, this));
        _modules.Add(new GameModeModule(plugin, this, admin));
        _modules.Add(new GConfigModule(plugin, this, admin));
        _modules.Add(new SpawnEditorModule(plugin, this, admin));
        _modules.Add(new SmallServerModule(plugin, this, admin));
        var duels = new DuelsModule(plugin, this, admin);
        _modules.Add(duels);
        var executes = new ExecutesModule(plugin, this, admin);
        _modules.Add(executes);
        _modules.Add(new FastStratModule(plugin, this, executes));
        _modules.Add(new EditModeModule(plugin, this, admin, duels, executes));
        _modules.Add(new SpotlightModule(plugin, this, admin));
        _modules.Add(new SpellTakersModule(plugin, this));
        // Added last so its map-start cvar re-apply lands after the rankings
        // ModeCvars pass — the !gmenu setup then stays consistent across maps.
        _modules.Add(new ServerControlModule(plugin, this, admin));
    }

    public void Load(bool hotReload)
    {
        foreach (var module in _modules)
        {
            try
            {
                module.Load(hotReload);
                Utils.Logger.LogInfo("Garden", $"Module loaded: {module.Name} (enabled: {module.Enabled})");
            }
            catch (Exception ex)
            {
                Utils.Logger.LogException($"Garden/{module.Name}", ex);
            }
        }
    }

    public void OnMapStart(string mapName)
    {
        foreach (var module in _modules)
        {
            try
            {
                module.OnMapStart(mapName);
            }
            catch (Exception ex)
            {
                Utils.Logger.LogException($"Garden/{module.Name}", ex);
            }
        }
    }

    public void Unload()
    {
        foreach (var module in _modules)
        {
            try
            {
                module.Unload();
            }
            catch (Exception ex)
            {
                Utils.Logger.LogException($"Garden/{module.Name}", ex);
            }
        }
    }
}
