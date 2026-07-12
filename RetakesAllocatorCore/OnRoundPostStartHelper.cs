using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using RetakesAllocatorCore.Config;
using RetakesAllocatorCore.Db;
using RetakesAllocatorCore.Managers;
using System;

namespace RetakesAllocatorCore;

public class OnRoundPostStartHelper
{
    public static void Handle<T>(
        ICollection<T> allPlayers,
        Func<T?, ulong> getSteamId,
        Func<T, CsTeam> getTeam,
        Action<T> giveDefuseKit,
        Action<T, ICollection<CsItem>, string?> allocateItemsForPlayer,
        Func<T, bool> isVip,
        out RoundType currentRoundType
    ) where T : notnull
    {
        var roundType = RoundTypeManager.Instance.GetNextRoundType();
        currentRoundType = roundType;

        // On a force-buy round, one random team force-buys while the other gets a
        // full buy - unless another plugin forced a specific team (eg. Competitive
        // Retakes' loss-streak rule).
        var forceBuyOverride = RoundTypeManager.Instance.ConsumeForceBuyTeamOverride();
        RoundTypeManager.Instance.SetForceBuyTeam(
            roundType == RoundType.HalfBuy
                ? forceBuyOverride ??
                  (CsTeam?) Utils.Choice(new List<CsTeam> {CsTeam.Terrorist, CsTeam.CounterTerrorist})
                : null
        );

        // The weapon change window opens now and starts counting once the round goes live.
        RoundTypeManager.Instance.ResetRoundLiveTime();

        var tEffectiveRoundType = RoundTypeManager.Instance.GetEffectiveRoundType(roundType, CsTeam.Terrorist);
        var ctEffectiveRoundType = RoundTypeManager.Instance.GetEffectiveRoundType(roundType, CsTeam.CounterTerrorist);

        var tPlayers = new List<T>();
        var ctPlayers = new List<T>();
        var playerIds = new List<ulong>();
        foreach (var player in allPlayers)
        {
            var steamId = getSteamId(player);
            if (steamId != 0)
            {
                playerIds.Add(steamId);
            }

            var playerTeam = getTeam(player);
            if (playerTeam == CsTeam.Terrorist)
            {
                tPlayers.Add(player);
            }
            else if (playerTeam == CsTeam.CounterTerrorist)
            {
                ctPlayers.Add(player);
            }
        }

        Log.Debug($"#T Players: {string.Join(",", tPlayers.Select(getSteamId))}");
        Log.Debug($"#CT Players: {string.Join(",", ctPlayers.Select(getSteamId))}");

        var userSettingsByPlayerId = Queries.GetUsersSettings(playerIds);

        var defusingPlayer = Utils.Choice(ctPlayers);

        HashSet<T> FilterByPreferredWeaponPreference(IEnumerable<T> ps) =>
            ps.Where(p =>
                    userSettingsByPlayerId.TryGetValue(getSteamId(p), out var userSetting) &&
                    userSetting.GetWeaponPreference(getTeam(p), WeaponAllocationType.Preferred) is not null)
                .ToHashSet();

        ICollection<T> tPreferredPlayers = new List<T>();
        ICollection<T> ctPreferredPlayers = new List<T>();

        Random random = new Random();
        double generatedChance = random.NextDouble() * 100;

        if (generatedChance <= Configs.GetConfigData().ChanceForPreferredWeapon)
        {
            // Preferred (sniper) selection applies to any team effectively on a full buy,
            // including the full-buying team of a force-buy round.
            if (tEffectiveRoundType == RoundType.FullBuy)
            {
                tPreferredPlayers =
                    WeaponHelpers.SelectPreferredPlayers(FilterByPreferredWeaponPreference(tPlayers), isVip,
                        CsTeam.Terrorist);
            }

            if (ctEffectiveRoundType == RoundType.FullBuy)
            {
                ctPreferredPlayers =
                    WeaponHelpers.SelectPreferredPlayers(FilterByPreferredWeaponPreference(ctPlayers), isVip,
                        CsTeam.CounterTerrorist);
            }
        }

        var forceBuyTeam = RoundTypeManager.Instance.ForceBuyTeam;
        var nadesByPlayer = new Dictionary<T, ICollection<CsItem>>();
        NadeHelpers.AllocateNadesToPlayers(
            NadeHelpers.GetUtilForTeam(
                RoundTypeManager.Instance.Map,
                tEffectiveRoundType,
                CsTeam.Terrorist,
                tPlayers.Count,
                isForceBuyTeam: forceBuyTeam == CsTeam.Terrorist
            ),
            tPlayers,
            nadesByPlayer
        );
        NadeHelpers.AllocateNadesToPlayers(
            NadeHelpers.GetUtilForTeam(
                RoundTypeManager.Instance.Map,
                ctEffectiveRoundType,
                CsTeam.CounterTerrorist,
                ctPlayers.Count,
                isForceBuyTeam: forceBuyTeam == CsTeam.CounterTerrorist
            ),
            ctPlayers,
            nadesByPlayer
        );

        foreach (var player in allPlayers)
        {
            var team = getTeam(player);
            var playerSteamId = getSteamId(player);
            userSettingsByPlayerId.TryGetValue(playerSteamId, out var userSetting);

            var effectiveRoundType = team switch
            {
                CsTeam.Terrorist => tEffectiveRoundType,
                CsTeam.CounterTerrorist => ctEffectiveRoundType,
                _ => roundType,
            };

            var givePreferred = team switch
            {
                CsTeam.Terrorist => tPreferredPlayers.Contains(player),
                CsTeam.CounterTerrorist => ctPreferredPlayers.Contains(player),
                _ => false,
            };

            var weapons = WeaponHelpers.GetWeaponsForRoundType(
                effectiveRoundType,
                team,
                userSetting,
                givePreferred
            );

            var items = new List<CsItem>
            {
                team == CsTeam.Terrorist ? CsItem.DefaultKnifeT : CsItem.DefaultKnifeCT,
            };
            items.AddRange(weapons);

            // Economy: on a pistol round, taking a non-default pistol costs you your kevlar.
            var giveArmor = true;
            if (effectiveRoundType == RoundType.Pistol && Configs.GetConfigData().EnablePistolRoundEconomy)
            {
                var defaultPistol = WeaponHelpers.GetDefaultPistol(team);
                var allocatedPistol = weapons
                    .Where(w => WeaponHelpers.GetSlotTypeForItem(w) == ItemSlotType.Secondary)
                    .Cast<CsItem?>()
                    .FirstOrDefault();
                if (defaultPistol is not null && allocatedPistol is not null &&
                    !WeaponHelpers.IsDefaultPistol(team, allocatedPistol.Value))
                {
                    giveArmor = false;
                }
            }

            if (giveArmor)
            {
                items.Insert(0, RoundTypeHelpers.GetArmorForRoundType(effectiveRoundType));
            }

            if (nadesByPlayer.TryGetValue(player, out var playerNades))
            {
                items.AddRange(playerNades);
            }

            if (team == CsTeam.CounterTerrorist)
            {
                // On non-pistol rounds, everyone gets defuse kit and util
                if (roundType != RoundType.Pistol)
                {
                    giveDefuseKit(player);
                }
                else if (getSteamId(defusingPlayer) == getSteamId(player))
                {
                    // On pistol rounds, only one person gets a defuse kit
                    giveDefuseKit(player);
                }
            }

            // Zeus: free for anyone who opted in (or for everyone if the server forces it)
            if (
                Configs.GetConfigData().ZeusPreference == ZeusPreference.Always ||
                (userSetting?.GetZeusPreference() ?? false)
            )
            {
                items.Add(CsItem.Zeus);
            }

            allocateItemsForPlayer(player, items, team == CsTeam.Terrorist ? "slot5" : "slot1");
        }
    }
}