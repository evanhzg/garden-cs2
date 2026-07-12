# Garden Retakes — Command Reference

Every command works from chat with `!` or `/` (e.g. `!guns`) and from console as `css_*`
(e.g. `css_guns`). **Levels**: Everyone · Mod · Admin · Owner (Garden admin registry;
`@css/root` counts as Owner). *Keep this file updated whenever commands change —
it feeds the website `/commands` page (ROADMAP Phase W).*

## Players — weapons (allocator)

| Command | Description |
|---|---|
| `!guns` | Open the chat weapon-preference menu (walks through all loadouts) |
| `!menu` *(configurable)* | Open the center weapon menu — W/S navigate, D select, A back, TAB exit |
| `!gun <name> [T\|CT]` | Set a weapon preference (partial names work) |
| `!removegun <name> [T\|CT]` | Remove a weapon preference |
| `!ak` / `!ak47` | AK-47 as full-buy primary on **both** teams (applies instantly in the buy window) |
| `!m4a4`, `!m4a1` / `!m4a1s` | Same, for the M4s (work on T side too) |
| `!awp` | Join/leave the AWP rotation |
| `!zeus` | Toggle a free Zeus every round |
| `!nextround` | Vote for the next round type (if enabled) |

## Players — ranking & stats

| Command | Description |
|---|---|
| `!elo` | Your CS Rating and ladder placement |
| `!stats [ranked]` | Your season stats (K/D, ADR, KAST, rating, clutches...) |
| `!top` | Season top 10 |
| `!rr` / `!ranked` | Start/stop Ranked Retakes (vote) — informational when auto mode is on |
| `!ry` / `!rn` | Accept / decline the ongoing vote (ranked or CR) |
| `!rankedstatus` | Is ranked active? |
| `!cr` | Start a Competitive Retakes match vote (2v2/3v3, locked sides, MR12) — repeat to cancel a live match |
| `!crtop` | Top CR duo/trio teams |
| `!<map>` (e.g. `!mirage`) | Instant map change (aliases from rankings config; blocked during ranked/CR for non-admins) |
| `!voices` | Toggle bombsite voice announcements |

## Players — inventory (Garden-inventory plugin)

| Command | Description |
|---|---|
| `!ws` | Refresh your skins from the website |
| `!wslogin` | Sign in to the inventory simulator from in-game |
| `!loadout <name>` | Switch your active website loadout (case-insensitive; applies to your current team) |
| `!loadout` | Open the center loadout menu (W/S navigate, D select, A/TAB exit) |
| `!loadout random` | Equip a random loadout (never the current one) |
| `!loadouts` | List all your loadouts (active one marked) |
| `!borrow <key>` | Import a shared loadout by its short key (from the website Share button) and equip it |
| `!spray` | Apply your graffiti |

## Admins — moderation (Garden admin system)

| Command | Level | Description |
|---|---|---|
| `!gadmin add <steamid\|name> <owner\|admin\|mod>` | Owner | Add/update an admin (DB + JSON persisted) |
| `!gadmin remove <steamid\|name>` | Owner | Remove an admin (config owners can't be removed) |
| `!gadmin list` | Admin | List all admins |
| `!gkick <name>` | Mod | Kick a player |
| `!gmap <map>` | Mod | Change map |
| `!gslay <name>` | Admin | Slay a player |
| `!grcon <command...>` | Owner | Run any server command |

All moderation actions are written to the `GardenAdminLog` DB table (who, what, when).
With `GardenSettings.Admin.EnableShortAliases` on (post-transition), the short names
`!admin`, `!kick`, `!slay`, `!map`, `!rcon` work too.

## Game modes

| Command | Level | Description |
|---|---|---|
| `!gamemode` / `!gmode [mode]` | Admin to change | Show or switch mode: retakes, duels, executes, faststrat, **edit** |
| `!gedit` | Admin | Edit mode: open/close the editor menu (R cycle · E select · TAB close — works while moving/noclipping). Menu scrolls (▲/▼) so all rows are reachable; Executes category can cycle a strategy's saved nades to **preview** (throws it) or **delete** them |
| `!name <...>` | Admin | Answer an editor name prompt (multi-word names OK, e.g. `!name A Site VS Long`) |
| `!duelscore` | Everyone | Duel scoreboard (Duels mode) |
| `!duelscore arenas` | Everyone | Per-arena stats (duels played, best fighter) |
| `!duel <player> [firstTo]` | Everyone | Challenge a player to a private duel (no rotation; first-to-X or infinite) |
| `!duel accept` / `decline` / `stop` | Everyone | Answer or cancel a challenge |
| `!garena new <name>` | Admin | Create a named duel arena (end A = where you stand) |
| `!garena setb <name>` / `seta <name>` | Admin | Set/re-set the arena ends |
| `!garena list` / `del <name>` | Admin | Manage named arenas |
| `!gexec new <a\|b> <name...>` | Admin | Create an execute strategy (multi-word names OK; starts editing it) |
| `!gexec edit <name>` | Admin | Edit an existing strategy |
| `!gexec tstart` / `!gexec ctsetup` | Admin | Add a T-start / CT-setup position where you stand |
| `!gexec nade [delay]` | Admin | Save your last thrown grenade as an auto-throw lineup (T or CT side = your side when thrown) |
| `!gexec weight <0-100>` | Admin | Random-pick weight of the edited strategy (0 = manual only) |
| `!gexec list` / `info [name]` / `del <name>` | Admin | Manage strategies |
| `!gexec play <name>` / `!gexec random` | Admin | Force one strategy / back to random |
| `!strat <name\|list>` | Everyone (T side) | Fast-strat: vote the T strategy for next round |
| `!setup <name\|list>` | Everyone (CT side) | Fast-strat: vote the CT setup for next round |
| `!smallserver <on\|off\|auto>` | Mod | Small-server overlay (auto = ON at ≤3 humans) |
| `!setnextround <P/H/F>` | Owner (`@css/root`) | Force next round type |
| `!forcebombsite <A\|B>` / `!forcebombsitestop` | root | Force retakes onto one site |
| `!scramble` | admin | Scramble teams next round |

## Admins — spawn editor (Garden, R1)

| Command | Level | Description |
|---|---|---|
| `!gspawns <a\|b\|all\|flag <name>\|off>` | Admin | Render spawns (models + labels with flags/author) |
| `!gspawn add <t\|ct> <a\|b> [duel\|smallserver\|execute\|planter ...]` | Admin | Place a spawn where you stand |
| `!gspawn del` / `!gspawn move` | Admin | Delete / move the nearest spawn |
| `!gspawn flag <name>` | Admin | Toggle a flag on the nearest spawn |
| `!gspawn info` | Admin | Nearest spawn details (incl. who placed it) |
| `!gspawn test [a\|b]` | Admin | Teleport through spawns (repeat = next) |
| `!gspawn round <a\|b\|off>` | Admin | Dry-run rounds on one site |

Base editor equivalents still exist: `!showspawns <A|B>`, `!add`, `!remove`, `!nearest`, `!hidespawns` (root).

## Admins — configuration & seasons

| Command | Level | Description |
|---|---|---|
| `!gconfig` | Admin | List config targets: retakes, garden, allocator, rankings |
| `!gconfig <target> [path]` | Admin | Browse a config (e.g. `!gconfig rankings ranked`) |
| `!gconfig <target> <path> <value>` | Owner | Set a value — live apply + saved to the JSON (e.g. `!gconfig rankings ranked.minplayers 4`) |
| `!season_new [name]` | root | Start a new season (archives the old one) |
| `!seasons` | root | List all seasons |
| `!rankings_reload_config` | root | Reload `config/rankings.json` |
| `!reload_allocator_config` | root | Reload the allocator `config/config.json` |
| `!print_config [name]` | root | Print allocator config |
| `!mapconfig <name>` / `!mapconfigs` | root | Force/list retakes map configs |
| `!debugqueues` | root | Dump the queue state |

## Spotlight (fun — watch a specific player)

Keeps an eye on configured player(s) (default: **Damien** / vz7y). Warns the CTs
when a watched T pushes a defined zone in the first seconds of the round, plus a
couple of gag effects. Config: `GardenSettings.Spotlight` (targets, alias,
`AlertWindowSeconds`, `AlertAudience`, `AutoReveal`, `AutoNoJump`). Zones are
per-map in `spotlight_zones/<map>.json`.

| Command | Level | Description |
|---|---|---|
| `!spotlight` | any | Show status (targets, alias, window, audience, zones, auto-flags) |
| `!pushzone list` | any | List this map's push zones |
| `!pushzone add <name> [radius]` | Admin | Save a zone at your feet (default radius 300u) |
| `!pushzone del <name>` / `!pushzone clear` | Admin | Remove one / all zones on the map |
| `!reveal [player] [seconds]` | Admin | Glow a player through walls for **everyone** (default target = Damien, default 30s) |
| `!nojump [player]` | Admin | Toggle: the player can't jump for the round (default target = Damien) |

When a watched player enters a zone in the alert window, everyone in the audience
(CTs by default) gets `⚠ Damien is pushing short!` in chat + center-screen, once
per zone per round.

## Test commands (root)

`!rr_force`, `!rr_stop`, `!rr_state`, `!rr_setelo <elo>`, `!rr_test_round [win|loss] [kills]`
