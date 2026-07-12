using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using RetakesAllocatorCore.Config;
using RetakesAllocatorCore;

namespace RetakesAllocator;

public static class Helpers
{
    public static bool PlayerIsValid(CCSPlayerController? player)
    {
        return player is not null && player.IsValid;
    }

    public static void WriteNewlineDelimited(string message, Action<string> writer)
    {
        foreach (var line in message.Split("\n"))
        {
            writer($"{PluginInfo.MessagePrefix}{line}");
        }
    }

    public static ICollection<string> CommandInfoToArgList(CommandInfo commandInfo, bool includeFirst = false)
    {
        var result = new List<string>();

        for (var i = includeFirst ? 0 : 1; i < commandInfo.ArgCount; i++)
        {
            result.Add(commandInfo.GetArg(i));
        }

        return result;
    }

    public static ulong GetSteamId(CCSPlayerController? player)
    {
        if (!PlayerIsValid(player))
        {
            return 0;
        }

        return player?.AuthorizedSteamID?.SteamId64 ?? 0;
    }

    public static CsTeam GetTeam(CCSPlayerController player)
    {
        return player.Team;
    }

    public static void RemoveArmor(CCSPlayerController player)
    {
        if (!PlayerIsValid(player) || player.PlayerPawn.Value?.ItemServices is null)
        {
            return;
        }

        var itemServices = new CCSPlayer_ItemServices(player.PlayerPawn.Value.ItemServices.Handle);
        itemServices.HasHelmet = false;
    }

    /// <summary>
    /// Gives kevlar (100 armor, no helmet) when the player has none.
    /// Returns true when armor was actually given.
    /// </summary>
    public static bool GiveKevlarIfMissing(CCSPlayerController player)
    {
        if (!PlayerIsValid(player) || player.PlayerPawn.Value is null)
        {
            return false;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn.ArmorValue > 0)
        {
            return false;
        }

        pawn.ArmorValue = 100;
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
        return true;
    }

    /// <summary>
    /// Fully strips kevlar and helmet (pistol-round economy rule).
    /// </summary>
    public static void StripArmor(CCSPlayerController player)
    {
        if (!PlayerIsValid(player) || player.PlayerPawn.Value is null)
        {
            return;
        }

        var pawn = player.PlayerPawn.Value;
        pawn.ArmorValue = 0;
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");

        if (pawn.ItemServices is not null)
        {
            var itemServices = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
            itemServices.HasHelmet = false;
        }
    }

    /// <summary>
    /// Freezes an alive player in place (used while the center gun menu is open).
    /// </summary>
    public static void FreezePlayer(CCSPlayerController player)
    {
        if (!PlayerIsValid(player) || !player.PawnIsAlive)
        {
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid || pawn.MoveType == MoveType_t.MOVETYPE_NONE)
        {
            return;
        }

        pawn.MoveType = MoveType_t.MOVETYPE_NONE;
        Schema.SetSchemaValue(pawn.Handle, "CBaseEntity", "m_nActualMoveType", 0);
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    }

    public static void UnfreezePlayer(CCSPlayerController player)
    {
        if (!PlayerIsValid(player))
        {
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid || pawn.MoveType == MoveType_t.MOVETYPE_WALK)
        {
            return;
        }

        pawn.MoveType = MoveType_t.MOVETYPE_WALK;
        Schema.SetSchemaValue(pawn.Handle, "CBaseEntity", "m_nActualMoveType", 2);
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    }

    public static CsItem? GetPlayerWeaponItem(CCSPlayerController player, Func<CsItem, bool> pred)
    {
        if (!PlayerIsValid(player) || player.PlayerPawn.Value?.WeaponServices is null)
        {
            return null;
        }

        foreach (var weapon in player.PlayerPawn.Value.WeaponServices.MyWeapons)
        {
            if (weapon is not {IsValid: true, Value.IsValid: true})
            {
                continue;
            }

            CsItem? item = Utils.ToEnum<CsItem>(weapon.Value.DesignerName);
            if (item is not null && pred(item.Value))
            {
                return item;
            }
        }

        return null;
    }

    public static CHandle<CBasePlayerWeapon>? GetPlayerWeapon(CCSPlayerController player,
        Func<CBasePlayerWeapon, CsItem, bool> pred)
    {
        if (!PlayerIsValid(player) || player.PlayerPawn.Value?.WeaponServices is null)
        {
            return null;
        }

        foreach (var weapon in player.PlayerPawn.Value.WeaponServices.MyWeapons)
        {
            if (weapon is not {IsValid: true, Value.IsValid: true})
            {
                continue;
            }

            CsItem? item = Utils.ToEnum<CsItem>(weapon.Value.DesignerName);
            if (item is not null && pred(weapon.Value, item.Value))
            {
                return weapon;
            }
        }

        return null;
    }

    public static bool RemoveWeapons(CCSPlayerController player, Func<CsItem, bool>? where = null)
    {
        if (!PlayerIsValid(player) || player.PlayerPawn.Value?.WeaponServices is null)
        {
            return false;
        }

        var pawn = player.PlayerPawn.Value;
        var weaponServices = pawn.WeaponServices!;

        var toRemove = new List<CBasePlayerWeapon>();

        foreach (var weapon in weaponServices.MyWeapons)
        {
            if (weapon is not {IsValid: true, Value.IsValid: true})
            {
                continue;
            }

            if (weapon.Value.DesignerName is "weapon_knife" or "weapon_knife_t")
            {
                continue;
            }

            CsItem? item = Utils.ToEnum<CsItem>(weapon.Value.DesignerName);
            if (
                where is not null &&
                (item is null || !where(item.Value))
            )
            {
                continue;
            }

            toRemove.Add(weapon.Value);
        }

        if (toRemove.Count == 0)
        {
            return false;
        }

        // Never delete the weapon the player is actively holding: force a knife
        // switch through the NORMAL selection path first. Writing m_hActiveWeapon
        // directly bypasses the deploy state machine and crashes the server on
        // the next weapon frame (the /ak crash).
        var activeWeapon = weaponServices.ActiveWeapon.Value;
        if (activeWeapon is not null &&
            toRemove.Any(w => w.Handle == activeWeapon.Handle) &&
            player.UserId is not null)
        {
            NativeAPI.IssueClientCommand((int) player.UserId, "slot3");
        }

        // Let the engine destroy the entities via a deferred "Kill" IO event
        // instead of Remove(): it safely handles weapons that are mid-deploy,
        // freshly created or still referenced by the networking snapshot.
        foreach (var weaponEntity in toRemove)
        {
            weaponEntity.AddEntityIOEvent("Kill", weaponEntity, null, "", 0.1f);
        }

        return true;
    }

    private static CCSGameRules? GetGameRules()
    {
        try
        {
            var gameRulesEntities = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules");
            return gameRulesEntities.First().GameRules;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsWarmup()
    {
        return GetGameRules()?.WarmupPeriod ?? false;
    }

    public static bool IsWeaponAllocationAllowed()
    {
        return WeaponHelpers.IsWeaponAllocationAllowed(GetGameRules()?.FreezePeriod ?? false);
    }

    public static double GetVectorDistance(Vector v1, Vector v2)
    {
        var dx = v1.X - v2.X;
        var dy = v1.Y - v2.Y;

        return Math.Sqrt(Math.Pow(dx, 2) + Math.Pow(dy, 2));
    }

    public static int GetNumPlayersOnTeam()
    {
        return Utilities.GetPlayers()
            .Where(player => player.IsValid)
            .Where(player => player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist).ToList()
            .Count;
    }

    public static bool IsWindows()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }

    public static bool IsVip(CCSPlayerController player) => AdminManager.PlayerHasPermissions(player, "@css/vip");

    public static async Task<bool> DownloadMissingFiles()
    {
        if (!Configs.GetConfigData().AutoUpdateSignatures)
        {
            return false;
        }
        string baseFolderPath = Configs.Shared.Module!;

        string gamedataFileName = "gamedata/RetakesAllocator_gamedata.json";
        string gamedataGithubUrl = "https://raw.githubusercontent.com/yonilerner/cs2-retakes-allocator/main/Resources/RetakesAllocator_gamedata.json";
        string gamedataFilePath = Path.Combine(baseFolderPath, gamedataFileName);
        string gamedataDirectoryPath = Path.GetDirectoryName(gamedataFilePath)!;
        
        return await CheckAndDownloadFile(gamedataFilePath, gamedataGithubUrl, gamedataDirectoryPath);
    }

    private static async Task<bool> CheckAndDownloadFile(string filePath, string githubUrl, string directoryPath)
    {
        if (!File.Exists(filePath))
        {
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
            await DownloadFileFromGithub(githubUrl, filePath);
            return true;
        }

        bool isFileDifferent = await IsFileDifferent(filePath, githubUrl);
        if (isFileDifferent)
        {
            File.Delete(filePath);
            await DownloadFileFromGithub(githubUrl, filePath);
            return true;
        }

        return false;
    }

    private static async Task<bool> IsFileDifferent(string localFilePath, string githubUrl)
    {
        try
        {
            byte[] localFileBytes = await File.ReadAllBytesAsync(localFilePath);
            string localFileHash = GetFileHash(localFileBytes);

            using (HttpClient client = new HttpClient())
            {
                byte[] githubFileBytes = await client.GetByteArrayAsync(githubUrl);
                string githubFileHash = GetFileHash(githubFileBytes);
                return localFileHash != githubFileHash;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Error comparing files: {ex.Message}");
            return false;
        }
    }

    private static string GetFileHash(byte[] fileBytes)
    {
        using (var md5 = System.Security.Cryptography.MD5.Create())
        {
            byte[] hashBytes = md5.ComputeHash(fileBytes);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
    }

    private static async Task DownloadFileFromGithub(string url, string destinationPath)
    {
        using (HttpClient client = new HttpClient())
        {
            try
            {
                byte[] fileBytes = await client.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(destinationPath, fileBytes);
            }
            catch (Exception ex)
            {
                Log.Warn($"Error downloading file: {ex.Message}");
            }
        }
    }
}
