using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using RetakesAllocatorCore;
using CounterStrikeSharp.API;
using System.Text;
using RetakesAllocator.Menus;
using RetakesAllocatorCore.Config;
using RetakesAllocatorCore.Db;
using RetakesAllocatorCore.Managers;
using static RetakesAllocatorCore.PluginInfo;
using CounterStrikeSharp.API.Modules.Events;

namespace RetakesAllocator.AdvancedMenus;

public class AdvancedGunMenu
{
    public Dictionary<ulong, bool> menuon = new Dictionary<ulong, bool>();
    public Dictionary<ulong, int> mainmenu = new Dictionary<ulong, int>();
    public Dictionary<ulong, int> currentIndexDict = new Dictionary<ulong, int>();
    public Dictionary<ulong, bool> buttonPressed = new Dictionary<ulong, bool>();
    private static void Print(CCSPlayerController player, string message)
    {
        Helpers.WriteNewlineDelimited(message, player.PrintToChat);
    }

    /// <summary>
    /// A weapon list is "active" when its weapons can actually be given to the player during
    /// the current round. Inactive lists are grayed out; selecting from them still saves the
    /// preference for the next applicable round.
    /// </summary>
    private static bool IsAllocationActiveForPlayer(CCSPlayerController player,
        WeaponAllocationType allocationType, CsTeam listTeam)
    {
        var effectiveRoundType = RoundTypeManager.Instance.GetCurrentEffectiveRoundType(player.Team);
        if (effectiveRoundType is null)
        {
            // No round info (eg. warmup): nothing to gray out
            return true;
        }

        if (player.Team != listTeam)
        {
            return false;
        }

        return WeaponHelpers.IsAllocationTypeValidForRound(allocationType, effectiveRoundType);
    }

    private static void AppendWeaponListLines(StringBuilder builder, string[] lines, int currentIndex,
        bool active, string imageLeft, string imageRight)
    {
        var highlightColor = active ? "orange" : "#9A9A9A";
        var normalColor = active ? "white" : "#777777";

        for (int i = 0; i < lines.Length; i++)
        {
            if (i == currentIndex)
            {
                builder.AppendLine($"<font color='{highlightColor}'>{imageLeft} {lines[i]} {imageRight}</font><br>");
            }
            else
            {
                builder.AppendLine($"<font color='{normalColor}'>{lines[i]}</font><br>");
            }
        }

        if (!active)
        {
            builder.AppendLine(
                $"<font color='#9A9A9A'>{Translator.Instance["menu.unavailable_this_round"]}</font><br>");
        }
    }
    /// <summary>
    /// Chat trigger for the center menu (called from the allocator's
    /// "say"/"say_team" listener — EventPlayerChat no longer exists in CSS).
    /// </summary>
    public void HandleChatMessage(CCSPlayerController player, string message)
    {
        if (player == null || !player.IsValid) return;
        var playerid = player.SteamID;

        if (string.IsNullOrWhiteSpace(message)) return;
        message = message.Trim();
        string[] CenterMenuCommands = Configs.GetConfigData().InGameGunMenuCenterCommands.Split(',');

        if (CenterMenuCommands.Any(cmd => cmd.Equals(message, StringComparison.OrdinalIgnoreCase)))
        {
            if (!menuon.ContainsKey(playerid))
            {
                menuon.Add(playerid, true);
            }
            if (!mainmenu.ContainsKey(playerid))
            {
                mainmenu.Add(playerid, 0);
            }
            if (!currentIndexDict.ContainsKey(playerid))
            {
                currentIndexDict.Add(playerid, 0);
            }
            if (!buttonPressed.ContainsKey(playerid))
            {
                buttonPressed.Add(playerid, false);
            }

            // No moving around while picking weapons.
            Helpers.FreezePlayer(player);
        }
    }

    private void CloseMenu(ulong playerid, CCSPlayerController player)
    {
        currentIndexDict.Remove(playerid);
        buttonPressed.Remove(playerid);
        mainmenu.Remove(playerid);
        menuon.Remove(playerid);
        Helpers.UnfreezePlayer(player);
    }

    public void OnTick()
    {
        var playerEntities = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller");
        foreach (var player in playerEntities)
        {
            if (player == null || !player.IsValid || !player.PawnIsAlive || player.IsBot || player.IsHLTV) continue;
            
            var playerid = player.SteamID;
            if (menuon.ContainsKey(playerid))
            {
                // Keep the player frozen for as long as the menu is open
                // (re-applied because respawns reset the move type).
                Helpers.FreezePlayer(player);

                string Imageleft = string.IsNullOrEmpty(Translator.Instance["menu.left.image"]) ? "" : Translator.Instance["menu.left.image"];
                string ImageRight = string.IsNullOrEmpty(Translator.Instance["menu.right.image"]) ? "" : Translator.Instance["menu.right.image"];
                string BottomMenu = string.IsNullOrEmpty(Translator.Instance["menu.bottom.text"]) ? "" : Translator.Instance["menu.bottom.text"];
                string BottomMenuOnpistol = string.IsNullOrEmpty(Translator.Instance["menu.bottom.text.pistol"]) ? "" : Translator.Instance["menu.bottom.text.pistol"];

                string[] Main = {
                    string.IsNullOrEmpty(Translator.Instance["menu.main.tloadout"]) ? "█░ T Loadout ░█" : Translator.Instance["menu.main.tloadout"],
                    string.IsNullOrEmpty(Translator.Instance["menu.main.ctloadout"]) ? "█░ CT Loadout ░█" : Translator.Instance["menu.main.ctloadout"],
                    string.IsNullOrEmpty(Translator.Instance["menu.main.awp"]) ? "█░ AWP ░█" : Translator.Instance["menu.main.awp"],
                    string.IsNullOrEmpty(Translator.Instance["menu.main.zeus"]) ? "█░ Zeus ░█" : Translator.Instance["menu.main.zeus"]
                };

                List<string> TFullBuyList = new List<string>();
                List<string> TSecondaryList = new List<string>();
                List<string> THalfBuyList = new List<string>();
                List<string> TPistolRoundList = new List<string>();

                List<string> CTFullBuyList = new List<string>();
                List<string> CTSecondaryList = new List<string>();
                List<string> CTHalfBuyList = new List<string>();
                List<string> CTPistolRoundList = new List<string>();

                string[] Tloadout = { 
                    string.IsNullOrEmpty(Translator.Instance["menu.tprimary"]) ? "█ T Primary █" : Translator.Instance["menu.tprimary"], 
                    string.IsNullOrEmpty(Translator.Instance["menu.tsecondary"]) ? "█ T Secondary █" : Translator.Instance["menu.tsecondary"],
                    string.IsNullOrEmpty(Translator.Instance["menu.tPistol"]) ? "█ T Pistol Round █" : Translator.Instance["menu.tPistol"],
                    string.IsNullOrEmpty(Translator.Instance["menu.tHalfbuy"]) ? "█ T Half Buy █" : Translator.Instance["menu.tHalfbuy"]
                };
                
                string[] CTloadout = { 
                    string.IsNullOrEmpty(Translator.Instance["menu.ctprimary"]) ? "█ CT Primary █" : Translator.Instance["menu.ctprimary"], 
                    string.IsNullOrEmpty(Translator.Instance["menu.ctsecondary"]) ? "█ CT Secondary █" : Translator.Instance["menu.ctsecondary"], 
                    string.IsNullOrEmpty(Translator.Instance["menu.ctPistol"]) ? "█ CT Pistol Round █" : Translator.Instance["menu.ctPistol"],
                    string.IsNullOrEmpty(Translator.Instance["menu.ctHalfbuy"]) ? "█ CT Half Buy █" : Translator.Instance["menu.ctHalfbuy"]
                };

                string[] AWP = {
                    string.IsNullOrEmpty(Translator.Instance["menu.awp.always"]) ? "Always" : Translator.Instance["menu.awp.always"],
                    string.IsNullOrEmpty(Translator.Instance["menu.awp.never"]) ? "Never" : Translator.Instance["menu.awp.never"]
                };

                string[] Zeus = {
                    string.IsNullOrEmpty(Translator.Instance["menu.zeus.always"]) ? "Always" : Translator.Instance["menu.zeus.always"],
                    string.IsNullOrEmpty(Translator.Instance["menu.zeus.never"]) ? "Never" : Translator.Instance["menu.zeus.never"]
                };


                foreach (var weapon in WeaponHelpers.GetPossibleWeaponsForAllocationType(WeaponAllocationType.FullBuyPrimary, CsTeam.Terrorist))
                {
                    TFullBuyList.Add(weapon.GetName());
                }
                foreach (var weapon in WeaponHelpers.GetPossibleWeaponsForAllocationType(WeaponAllocationType.Secondary, CsTeam.Terrorist))
                {
                    TSecondaryList.Add(weapon.GetName());
                }
                foreach (var weapon in WeaponHelpers.GetPossibleWeaponsForAllocationType(WeaponAllocationType.HalfBuyPrimary, CsTeam.Terrorist))
                {
                    THalfBuyList.Add(weapon.GetName());
                }
                foreach (var weapon in WeaponHelpers.GetPossibleWeaponsForAllocationType(WeaponAllocationType.PistolRound, CsTeam.Terrorist))
                {
                    TPistolRoundList.Add(weapon.GetName());
                }

                foreach (var weapon in WeaponHelpers.GetPossibleWeaponsForAllocationType(WeaponAllocationType.FullBuyPrimary, CsTeam.CounterTerrorist))
                {
                    CTFullBuyList.Add(weapon.GetName());
                }
                foreach (var weapon in WeaponHelpers.GetPossibleWeaponsForAllocationType(WeaponAllocationType.Secondary, CsTeam.CounterTerrorist))
                {
                    CTSecondaryList.Add(weapon.GetName());
                }
                foreach (var weapon in WeaponHelpers.GetPossibleWeaponsForAllocationType(WeaponAllocationType.HalfBuyPrimary, CsTeam.CounterTerrorist))
                {
                    CTHalfBuyList.Add(weapon.GetName());
                }
                foreach (var weapon in WeaponHelpers.GetPossibleWeaponsForAllocationType(WeaponAllocationType.PistolRound, CsTeam.CounterTerrorist))
                {
                    CTPistolRoundList.Add(weapon.GetName());
                }

                string[] TFullBuy = TFullBuyList.ToArray();
                string[] TSecondary = TSecondaryList.ToArray();
                string[] THalfBuy = THalfBuyList.ToArray();
                string[] TPistolRound = TPistolRoundList.ToArray();

                string[] CTFullBuy = CTFullBuyList.ToArray();
                string[] CTSecondary = CTSecondaryList.ToArray();
                string[] CTHalfBuy = CTHalfBuyList.ToArray();
                string[] CTPistolRound = CTPistolRoundList.ToArray();

                if (player.Buttons == 0)
                {
                    buttonPressed[playerid] = false;
                }
                else if (player.Buttons == PlayerButtons.Back && !buttonPressed[playerid])
                {
                    if (mainmenu[playerid] == 0)//main
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == Main.Length - 1) ? 0 : currentIndexDict[playerid] + 1;
                    }

                    if (mainmenu[playerid] == 1)//T loadout
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == Tloadout.Length - 1) ? 0 : currentIndexDict[playerid] + 1;
                    }
                    if (mainmenu[playerid] == 2)//T Primary
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == TFullBuy.Length - 1) ? 0 : currentIndexDict[playerid] + 1;
                    }
                    if (mainmenu[playerid] == 3)//T Secondary
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == TSecondary.Length - 1) ? 0 : currentIndexDict[playerid] + 1;
                    }
                    if (mainmenu[playerid] == 4)//T Pistol Round
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == TPistolRound.Length - 1) ? 0 : currentIndexDict[playerid] + 1;
                    }
                    if (mainmenu[playerid] == 10)//T Half Buy
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == THalfBuy.Length - 1) ? 0 : currentIndexDict[playerid] + 1;
                    }

                    if (mainmenu[playerid] == 5)//CT loadout
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == CTloadout.Length - 1) ? 0 : currentIndexDict[playerid] + 1;
                    }
                    if (mainmenu[playerid] == 6)//CT Primary
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == CTFullBuy.Length - 1) ? 0 : currentIndexDict[playerid] + 1;
                    }
                    if (mainmenu[playerid] == 7)//CT Secondary
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == CTSecondary.Length - 1) ? 0 : currentIndexDict[playerid] + 1;
                    }
                    if (mainmenu[playerid] == 8)//CT Pistol Round
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == CTPistolRound.Length - 1) ? 0 : currentIndexDict[playerid] + 1;
                    }

                    if (mainmenu[playerid] == 9)//AWP loadout
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == AWP.Length - 1) ? 0 : currentIndexDict[playerid] + 1;
                    }
                    if (mainmenu[playerid] == 11)//CT Half Buy
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == CTHalfBuy.Length - 1) ? 0 : currentIndexDict[playerid] + 1;
                    }
                    if (mainmenu[playerid] == 12)//Zeus
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == Zeus.Length - 1) ? 0 : currentIndexDict[playerid] + 1;
                    }

                    buttonPressed[playerid] = true;
                    player.ExecuteClientCommand("play sounds/ui/csgo_ui_contract_type4.vsnd_c");
                }
                else if (player.Buttons == PlayerButtons.Forward && !buttonPressed[playerid])
                {
                    if (mainmenu[playerid] == 0)//main
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == 0) ? Main.Length - 1 : currentIndexDict[playerid] - 1;
                    }

                    if (mainmenu[playerid] == 1)//T loadout
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == 0) ? Tloadout.Length - 1 : currentIndexDict[playerid] - 1;
                    }
                    if (mainmenu[playerid] == 2)//T Primary
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == 0) ? TFullBuy.Length - 1 : currentIndexDict[playerid] - 1;
                    }
                    if (mainmenu[playerid] == 3)//T Secondary
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == 0) ? TSecondary.Length - 1 : currentIndexDict[playerid] - 1;
                    }
                    if (mainmenu[playerid] == 4)//T Pistol Round
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == 0) ? TPistolRound.Length - 1 : currentIndexDict[playerid] - 1;
                    }
                    if (mainmenu[playerid] == 10)//T Half Buy
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == 0) ? THalfBuy.Length - 1 : currentIndexDict[playerid] - 1;
                    }

                    if (mainmenu[playerid] == 5)//CT loadout
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == 0) ? CTloadout.Length - 1 : currentIndexDict[playerid] - 1;
                    }
                    if (mainmenu[playerid] == 6)//CT Primary
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == 0) ? CTFullBuy.Length - 1 : currentIndexDict[playerid] - 1;
                    }
                    if (mainmenu[playerid] == 7)//CT Secondary
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == 0) ? CTSecondary.Length - 1 : currentIndexDict[playerid] - 1;
                    }
                    if (mainmenu[playerid] == 8)//CT Pistol Round
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == 0) ? CTPistolRound.Length - 1 : currentIndexDict[playerid] - 1;
                    }

                    if (mainmenu[playerid] == 9)//AWP loadout
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == 0) ? AWP.Length - 1 : currentIndexDict[playerid] - 1;
                    }
                    if (mainmenu[playerid] == 11)//CT Half Buy
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == 0) ? CTHalfBuy.Length - 1 : currentIndexDict[playerid] - 1;
                    }
                    if (mainmenu[playerid] == 12)//Zeus
                    {
                        currentIndexDict[playerid] = (currentIndexDict[playerid] == 0) ? Zeus.Length - 1 : currentIndexDict[playerid] - 1;
                    }

                    buttonPressed[playerid] = true;
                    player.ExecuteClientCommand("play sounds/ui/csgo_ui_contract_type4.vsnd_c");
                }else if (player.Buttons == PlayerButtons.Moveright && !buttonPressed[playerid])
                {
                    int currentLineIndex = currentIndexDict[playerid];

                    if (mainmenu[playerid] == 2)
                    {
                        string currentLineTFullBuy = TFullBuy[currentLineIndex];
                        GunsMenu.HandlePreferenceSelection(player, CsTeam.Terrorist, currentLineTFullBuy);
                        Print(player, Translator.Instance["guns_menu.weapon_selected",currentLineTFullBuy, Utils.TeamString(CsTeam.Terrorist),Translator.Instance["weapon_type.primary"]]);
                    }
                    if (mainmenu[playerid] == 3)
                    {
                        string currentLineTSec = TSecondary[currentLineIndex];
                        GunsMenu.HandlePreferenceSelection(player, CsTeam.Terrorist, currentLineTSec, RoundType.FullBuy);
                        Print(player, Translator.Instance["guns_menu.weapon_selected",currentLineTSec, Utils.TeamString(CsTeam.Terrorist),Translator.Instance["weapon_type.secondary"]]);
                    }
                    if (mainmenu[playerid] == 4)
                    {
                        string currentLineTPR = TPistolRound[currentLineIndex];
                        GunsMenu.HandlePreferenceSelection(player, CsTeam.Terrorist, currentLineTPR);
                        Print(player, Translator.Instance["guns_menu.weapon_selected",currentLineTPR, Utils.TeamString(CsTeam.Terrorist),Translator.Instance["roundtype.Pistol"]]);
                    }
                    if (mainmenu[playerid] == 10)
                    {
                        string currentLineTHalf = THalfBuy[currentLineIndex];
                        GunsMenu.HandlePreferenceSelection(player, CsTeam.Terrorist, currentLineTHalf);
                        Print(player, Translator.Instance["guns_menu.weapon_selected",currentLineTHalf, Utils.TeamString(CsTeam.Terrorist),Translator.Instance["roundtype.HalfBuy"]]);
                    }
                    if (mainmenu[playerid] == 11)
                    {
                        string currentLineCTHalf = CTHalfBuy[currentLineIndex];
                        GunsMenu.HandlePreferenceSelection(player, CsTeam.CounterTerrorist, currentLineCTHalf);
                        Print(player, Translator.Instance["guns_menu.weapon_selected",currentLineCTHalf, Utils.TeamString(CsTeam.CounterTerrorist),Translator.Instance["roundtype.HalfBuy"]]);
                    }

                    if (mainmenu[playerid] == 6)
                    {
                        string currentLineCTFullBuy = CTFullBuy[currentLineIndex];
                        GunsMenu.HandlePreferenceSelection(player, CsTeam.CounterTerrorist, currentLineCTFullBuy);
                        Print(player, Translator.Instance["guns_menu.weapon_selected",currentLineCTFullBuy, Utils.TeamString(CsTeam.CounterTerrorist),Translator.Instance["weapon_type.primary"]]);
                    }
                    if (mainmenu[playerid] == 7)
                    {
                        string currentLineCTSec = CTSecondary[currentLineIndex];
                        GunsMenu.HandlePreferenceSelection(player, CsTeam.CounterTerrorist, currentLineCTSec, RoundType.FullBuy);
                        Print(player, Translator.Instance["guns_menu.weapon_selected",currentLineCTSec, Utils.TeamString(CsTeam.CounterTerrorist),Translator.Instance["weapon_type.secondary"]]);
                    }
                    if (mainmenu[playerid] == 8)
                    {
                        string currentLineCTPR = CTPistolRound[currentLineIndex];
                        GunsMenu.HandlePreferenceSelection(player, CsTeam.CounterTerrorist, currentLineCTPR);
                        Print(player, Translator.Instance["guns_menu.weapon_selected",currentLineCTPR, Utils.TeamString(CsTeam.CounterTerrorist),Translator.Instance["roundtype.Pistol"]]);
                    }

                    if (mainmenu[playerid] == 9)
                    {
                        string currentLineName = AWP[currentLineIndex];
                        if (currentLineName == AWP[0])
                        {
                            GunsMenu.HandlePreferenceSelection(player, CsTeam.Terrorist, CsItem.AWP.ToString(), remove: false);
                            Print(player, Translator.Instance["guns_menu.awp_preference_selected",currentLineName]);
                        }
                        if (currentLineName == AWP[1])
                        {
                            GunsMenu.HandlePreferenceSelection(player, CsTeam.Terrorist, CsItem.AWP.ToString(), remove: true);
                            Print(player, Translator.Instance["guns_menu.awp_preference_selected",currentLineName]);
                        }
                    }

                    if (mainmenu[playerid] == 12)
                    {
                        // Zeus is free and team-agnostic; apply it immediately.
                        var zeusEnabled = currentLineIndex == 0;
                        Queries.SetZeusPreference(playerid, zeusEnabled);
                        AllocatorModule.Instance?.ApplyZeusChangeNow(player, zeusEnabled);
                        Print(player, Translator.Instance[zeusEnabled ? "zeus.enabled" : "zeus.disabled"]);
                    }

                    if (mainmenu[playerid] == 5)
                    {
                        string currentLineName = CTloadout[currentLineIndex];
                        if (currentLineName == CTloadout[0])
                        {
                            mainmenu[playerid] = 6;
                            currentIndexDict[playerid] = 0;
                        }
                        if (currentLineName == CTloadout[1])
                        {
                            mainmenu[playerid] = 7;
                            currentIndexDict[playerid] = 0;
                        }
                        if (currentLineName == CTloadout[2])
                        {
                            mainmenu[playerid] = 8;
                            currentIndexDict[playerid] = 0;
                        }
                        if (CTHalfBuy.Length > 0 && currentLineName == CTloadout[3])
                        {
                            mainmenu[playerid] = 11;
                            currentIndexDict[playerid] = 0;
                        }
                    }
                    if (mainmenu[playerid] == 1)
                    {
                        string currentLineName = Tloadout[currentLineIndex];
                        if (currentLineName == Tloadout[0])
                        {
                            mainmenu[playerid] = 2;
                            currentIndexDict[playerid] = 0;
                        }
                        if (currentLineName == Tloadout[1])
                        {
                            mainmenu[playerid] = 3;
                            currentIndexDict[playerid] = 0;
                        }
                        if (currentLineName == Tloadout[2])
                        {
                            mainmenu[playerid] = 4;
                            currentIndexDict[playerid] = 0;
                        }
                        if (THalfBuy.Length > 0 && currentLineName == Tloadout[3])
                        {
                            mainmenu[playerid] = 10;
                            currentIndexDict[playerid] = 0;
                        }
                    }
                    if (mainmenu[playerid] == 0)
                    {
                        string currentLineName = Main[currentLineIndex];
                        if (currentLineName == Main[0])
                        {
                            mainmenu[playerid] = 1;
                            currentIndexDict[playerid] = 0;
                        }
                        if (currentLineName == Main[1])
                        {
                            mainmenu[playerid] = 5;
                            currentIndexDict[playerid] = 0;
                        }
                        if (currentLineName == Main[2])
                        {
                            mainmenu[playerid] = 9;
                            currentIndexDict[playerid] = 0;
                        }
                        if (currentLineName == Main[3])
                        {
                            mainmenu[playerid] = 12;
                            currentIndexDict[playerid] = 0;
                        }
                    }
                    buttonPressed[playerid] = true;
                    player.ExecuteClientCommand("play sounds/ui/item_sticker_select.vsnd_c");
                }
                else if ((player.Buttons == PlayerButtons.Moveleft || (long)player.Buttons == 8589934592) && !buttonPressed[playerid])
                {
                    // A (or TAB) = go back; at the main menu it closes the menu.
                    if (mainmenu[playerid] == 0)
                    {
                        CloseMenu(playerid, player);
                    }

                    if(mainmenu.ContainsKey(playerid))
                    {
                        if (mainmenu[playerid] == 1)
                        {
                            mainmenu[playerid] = 0;
                            currentIndexDict[playerid] = 0;
                        }
                        if (mainmenu[playerid] == 5)
                        {
                            mainmenu[playerid] = 0;
                            currentIndexDict[playerid] = 0;
                        }

                        if (mainmenu[playerid] == 2)
                        {
                            mainmenu[playerid] = 1;
                            currentIndexDict[playerid] = 0;
                        }
                        if (mainmenu[playerid] == 3)
                        {
                            mainmenu[playerid] = 1;
                            currentIndexDict[playerid] = 0;
                        }
                        if (mainmenu[playerid] == 4)
                        {
                            mainmenu[playerid] = 1;
                            currentIndexDict[playerid] = 0;
                        }
                        if (mainmenu[playerid] == 10)
                        {
                            mainmenu[playerid] = 1;
                            currentIndexDict[playerid] = 0;
                        }
                        if (mainmenu[playerid] == 11)
                        {
                            mainmenu[playerid] = 1;
                            currentIndexDict[playerid] = 0;
                        }

                        if (mainmenu[playerid] == 6)
                        {
                            mainmenu[playerid] = 5;
                            currentIndexDict[playerid] = 0;
                        }
                        if (mainmenu[playerid] == 7)
                        {
                            mainmenu[playerid] = 5;
                            currentIndexDict[playerid] = 0;
                        }
                        if (mainmenu[playerid] == 8)
                        {
                            mainmenu[playerid] = 5;
                            currentIndexDict[playerid] = 0;
                        }

                        if (mainmenu[playerid] == 9)
                        {
                            mainmenu[playerid] = 0;
                            currentIndexDict[playerid] = 0;
                        }

                        if (mainmenu[playerid] == 12)
                        {
                            mainmenu[playerid] = 0;
                            currentIndexDict[playerid] = 0;
                        }
                    }

                    if (menuon.ContainsKey(playerid))
                    {
                        buttonPressed[playerid] = true;
                    }

                    player.ExecuteClientCommand("play sounds/ui/menu_focus.vsnd_c");
                }

                StringBuilder builder = new StringBuilder();
                if (mainmenu.ContainsKey(playerid))
                {
                    if(mainmenu[playerid] == 0)
                    {
                        for (int i = 0; i < Main.Length; i++)
                        {
                            if (i == currentIndexDict[playerid])
                            {
                                string lineHtml = $"<font color='orange'>{Imageleft} {Main[i]} {ImageRight}</font><br>";
                                builder.AppendLine(lineHtml);
                            }
                            else
                            {
                                builder.AppendLine($"<font color='white'>{Main[i]}</font><br>");
                            }
                        }
                        builder.AppendLine(BottomMenu);
                    }

                    if(mainmenu[playerid] == 1)
                    {
                        for (int i = 0; i < Tloadout.Length; i++)
                        {
                            if (i == currentIndexDict[playerid]) 
                            {
                                string lineHtml = $"<font color='orange'>{Imageleft} {Tloadout[i]} {ImageRight}</font><br>";
                                builder.AppendLine(lineHtml);
                            }
                            else
                            {
                                builder.AppendLine($"<font color='white'>{Tloadout[i]}</font><br>");
                            }
                        }
                        builder.AppendLine(BottomMenu);
                    }
                    if(mainmenu[playerid] == 5)
                    {
                        for (int i = 0; i < CTloadout.Length; i++)
                        {
                            if (i == currentIndexDict[playerid]) 
                            {
                                string lineHtml = $"<font color='orange'>{Imageleft} {CTloadout[i]} {ImageRight}</font><br>";
                                builder.AppendLine(lineHtml);
                            }
                            else
                            {
                                builder.AppendLine($"<font color='white'>{CTloadout[i]}</font><br>");
                            }
                        }
                        builder.AppendLine(BottomMenu);
                    }
                    if(mainmenu[playerid] == 9)
                    {
                        for (int i = 0; i < AWP.Length; i++)
                        {
                            if (i == currentIndexDict[playerid])
                            {
                                string lineHtml = $"<font color='orange'>{Imageleft} {AWP[i]} {ImageRight}</font><br>";
                                builder.AppendLine(lineHtml);
                            }
                            else
                            {
                                builder.AppendLine($"<font color='white'>{AWP[i]}</font><br>");
                            }
                        }
                        builder.AppendLine(BottomMenu);
                    }

                    if(mainmenu[playerid] == 12)
                    {
                        for (int i = 0; i < Zeus.Length; i++)
                        {
                            if (i == currentIndexDict[playerid])
                            {
                                string lineHtml = $"<font color='orange'>{Imageleft} {Zeus[i]} {ImageRight}</font><br>";
                                builder.AppendLine(lineHtml);
                            }
                            else
                            {
                                builder.AppendLine($"<font color='white'>{Zeus[i]}</font><br>");
                            }
                        }
                        builder.AppendLine(BottomMenu);
                    }

                    if(mainmenu[playerid] == 2)
                    {
                        AppendWeaponListLines(builder, TFullBuy, currentIndexDict[playerid],
                            IsAllocationActiveForPlayer(player, WeaponAllocationType.FullBuyPrimary, CsTeam.Terrorist),
                            Imageleft, ImageRight);
                        builder.AppendLine(BottomMenu);
                    }
                    if(mainmenu[playerid] == 3)
                    {
                        AppendWeaponListLines(builder, TSecondary, currentIndexDict[playerid],
                            IsAllocationActiveForPlayer(player, WeaponAllocationType.Secondary, CsTeam.Terrorist),
                            Imageleft, ImageRight);
                        builder.AppendLine(BottomMenuOnpistol);
                    }
                    if(mainmenu[playerid] == 4)
                    {
                        AppendWeaponListLines(builder, TPistolRound, currentIndexDict[playerid],
                            IsAllocationActiveForPlayer(player, WeaponAllocationType.PistolRound, CsTeam.Terrorist),
                            Imageleft, ImageRight);
                        builder.AppendLine(BottomMenuOnpistol);
                    }
                    if(mainmenu[playerid] == 10)
                    {
                        AppendWeaponListLines(builder, THalfBuy, currentIndexDict[playerid],
                            IsAllocationActiveForPlayer(player, WeaponAllocationType.HalfBuyPrimary, CsTeam.Terrorist),
                            Imageleft, ImageRight);
                        builder.AppendLine(BottomMenu);
                    }
                    if(mainmenu[playerid] == 11)
                    {
                        AppendWeaponListLines(builder, CTHalfBuy, currentIndexDict[playerid],
                            IsAllocationActiveForPlayer(player, WeaponAllocationType.HalfBuyPrimary, CsTeam.CounterTerrorist),
                            Imageleft, ImageRight);
                        builder.AppendLine(BottomMenu);
                    }

                    if(mainmenu[playerid] == 6)
                    {
                        AppendWeaponListLines(builder, CTFullBuy, currentIndexDict[playerid],
                            IsAllocationActiveForPlayer(player, WeaponAllocationType.FullBuyPrimary, CsTeam.CounterTerrorist),
                            Imageleft, ImageRight);
                        builder.AppendLine(BottomMenu);
                    }
                    if(mainmenu[playerid] == 7)
                    {
                        AppendWeaponListLines(builder, CTSecondary, currentIndexDict[playerid],
                            IsAllocationActiveForPlayer(player, WeaponAllocationType.Secondary, CsTeam.CounterTerrorist),
                            Imageleft, ImageRight);
                        builder.AppendLine(BottomMenuOnpistol);
                    }
                    if(mainmenu[playerid] == 8)
                    {
                        AppendWeaponListLines(builder, CTPistolRound, currentIndexDict[playerid],
                            IsAllocationActiveForPlayer(player, WeaponAllocationType.PistolRound, CsTeam.CounterTerrorist),
                            Imageleft, ImageRight);
                        builder.AppendLine(BottomMenuOnpistol);
                    }

                }
                builder.AppendLine("</div>");
                var centerhtml = builder.ToString();
                player?.PrintToCenterHtml(centerhtml);
            }
        }
    }
    public HookResult OnEventPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event == null) return HookResult.Continue;
        var player = @event.Userid;

        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)return HookResult.Continue;
        var playerid = player.SteamID;
        menuon.Remove(playerid);
        mainmenu.Remove(playerid);
        currentIndexDict.Remove(playerid);
        buttonPressed.Remove(playerid);

        return HookResult.Continue;
    }
}
