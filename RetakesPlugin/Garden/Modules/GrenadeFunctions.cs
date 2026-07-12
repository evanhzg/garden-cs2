using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

namespace RetakesPlugin.Garden.Modules;

/// <summary>
/// Native grenade-projectile Create() functions. Calling the game's own
/// CXGrenadeProjectile::Create() makes a spawned nade fly the recorded trajectory
/// AND detonate exactly like a real throw (CreateEntityByName can't for smoke/HE).
///
/// Signatures are GAME-BUILD SPECIFIC. These are the current-build signatures
/// (CSS community gamedata, 2026-07). If a CS2 update breaks executes/fast-strat
/// utility again, replace the byte patterns below (Smoke/HE) with the new ones.
///
/// Resolution is LAZY + GUARDED: a wrong/missing signature yields null (the caller
/// falls back to CreateEntityByName) instead of throwing at plugin load.
/// </summary>
public static class GrenadeFunctions
{
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    private static readonly string SmokeSig = IsLinux
        ? "55 4C 89 C1 48 89 E5 41 57 49 89 FF 41 56 45 89 CE 41 55 4D 89 C5 41 54 53 48 83 EC ? 48 89 55 ? 48 89 F2 48 89 FE"
        : "48 8B C4 48 89 58 ? 48 89 68 ? 48 89 70 ? 57 41 56 41 57 48 81 EC ? ? ? ? 48 8B B4 24 ? ? ? ? 4D 8B F8";

    private static readonly string HeSig = IsLinux
        ? "55 4C 89 C1 48 89 E5 41 57 49 89 FF 41 56 49 89 D6 48 89 F2 48 89 FE 41 55 48 8D 3D ? ? ? ? 4D 89 C5 41 54 45 89 CC 53"
        : "48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 57 48 83 EC ? 48 8B 6C 24 ? 49 8B F8 4C 8B C2 0F 29 74 24 ? 48 8B D1 48 8B D9 48 8D 0D ? ? ? ? 4C 8B CD E8 ? ? ? ? F3 0F 10 0D ? ? ? ? 48 8B C8 48 8B F0 E8 ? ? ? ? 48 8B D7 48 8B CE";

    private static MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, int, CSmokeGrenadeProjectile>? _smoke;
    private static MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, CHEGrenadeProjectile>? _he;
    private static bool _smokeInit;
    private static bool _heInit;

    public static MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, int, CSmokeGrenadeProjectile>? Smoke
    {
        get
        {
            if (!_smokeInit)
            {
                _smokeInit = true;
                try { _smoke = new(SmokeSig); }
                catch { _smoke = null; }
            }
            return _smoke;
        }
    }

    public static MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, CHEGrenadeProjectile>? He
    {
        get
        {
            if (!_heInit)
            {
                _heInit = true;
                try { _he = new(HeSig); }
                catch { _he = null; }
            }
            return _he;
        }
    }
}
