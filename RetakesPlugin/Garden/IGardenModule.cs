namespace RetakesPlugin.Garden;

/// <summary>
/// A Garden feature module. Modules are constructed once at plugin load,
/// register their own commands/event handlers in <see cref="Load"/>, and get
/// map lifecycle callbacks from <see cref="GardenHost"/>.
/// </summary>
public interface IGardenModule
{
    string Name { get; }

    bool Enabled { get; }

    /// <summary>Called once at plugin load (register commands + event handlers here).</summary>
    void Load(bool hotReload);

    /// <summary>Called on every map start (after base retakes services are initialized).</summary>
    void OnMapStart(string mapName);

    void Unload();
}
