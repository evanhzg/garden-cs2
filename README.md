<div align="center">

# Garden CS2 Plugin

**A feature-complete CS2 retakes plugin — rankings, inventory, duels, executes, and more.**

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg?style=flat-square)](LICENSE)
[![Website Repo](https://img.shields.io/badge/Website%20repo-garden--retakes--website-orange?style=flat-square)](https://github.com/evanhzg/garden-retakes-website)

</div>

---

Garden CS2 / Garden Retakes is the all-in-one CS2 plugin powering **[retakes.fr](https://retakes.fr)** and is made on top of [CounterStrikeSharp](https://github.com/roflmuffin/counterstrikesharp) by roflmuffin, all credits go to the author and the talented contributors to this wonderful framework.

The website is linked to a private server, your data won't be linked.

It started as a fork of [b3none/cs2-retakes](https://github.com/b3none/cs2-retakes) and has since grown into a full platform: weapon allocator, ELO/season rankings, Competitive Retakes, Duels, Executes, Fast-Strat, an in-game admin system, instant defuse, a visual spawn editor with mode flags, and a fun Spotlight module — all integrated into a single deployable plugin.

The companion website ([retakes.fr](https://retakes.fr) · [repo](https://github.com/evanhzg/garden-retakes-website)) reads the same MySQL database and provides a season ladder, deep per-player stats, an in-game skin builder (Inventory Simulator), a web-based admin panel, and match history.


#### Author's (myself, then) personal note and AI notice
This entire project along the website were both made nearly entirely using AI. This started as a very personal project to try new abilities offered by Fable 5 and other models compatibilities. I am, as a person, concerned and careful about AI usage and development. It's current form might have a big impact both beneficial and devastating towards dev communities such as the open-source one.

I do not consider this project 100% mine as I used this technology for most of it but I guess making it open-source could still benefit some of the plugin developers or Counter-Strike players wanting a set of tools to enjoy with a single installation on their private servers.

---

## Table of Contents

- [Features](#features)
- [Architecture](#architecture)
- [Prerequisites](#prerequisites)
- [Installation](#installation)
- [Configuration](#configuration)
- [Commands](#commands)
- [Game Modes](#game-modes)
- [Building from Source](#building-from-source)
- [Credits & Upstream](#credits--upstream)
- [License](#license)

---

## Features

### Core (from b3none/cs2-retakes)
- Bombsite selection & per-map configurations
- Spawn system with retakes queue and team management
- Equipment allocation with CT/T preferences
- VIP queue priority / immunity tiers
- Auto-plant, auto-scramble, team balance

### Garden Extensions

| Module | Status | Description |
|---|---|---|
| **Allocator** | ✅ Stable | Full weapon allocator port — chat menus (`!guns`, `!menu`), per-weapon preferences persisted in DB, AWP rotation, Zeus toggle |
| **Rankings** | ✅ Stable | Season-based ELO (CS Rating), HLTV-style rating, K/D/ADR/KAST/clutch tracking, ranked toggle via vote, top-10 ladders |
| **Competitive Retakes (CR)** | ✅ Stable | 2v2/3v3 locked-side matches (MR12), team ELO, per-duo/trio match history |
| **Admin System** | ✅ Stable | DB-backed admin registry (Owner / Admin / Mod), full audit log in `GardenAdminLog`, kick/slay/map/rcon commands |
| **Instant Defuse** | ✅ Stable | Defuse completes instantly when no utility danger is present |
| **Spawn Editor** | ✅ Stable | Visual editor with per-spawn mode flags (`duel`, `smallserver`, `execute`, `planter`), author tracking |
| **Game Mode Switcher** | ✅ Stable | Switch between Retakes / Duels / Executes / Fast-Strat / Edit via `!gamemode` |
| **Duels** | ✅ Stable | Named arenas, parallel lanes, private challenges, spectator auto-follow |
| **Executes** | ✅ Stable | Recorded T-side execute strategies with auto-thrown grenade lineups |
| **Fast-Strat** | ✅ Stable | Players vote on T strategy / CT setup for the next round |
| **Small Server Overlay** | ✅ Stable | Activates automatically at ≤ N humans; adjusted spawns, nade limits, instant round switch |
| **GConfig** | ✅ Stable | Browse and hot-edit any config section live in-game via `!gconfig` |
| **Spotlight** | ✅ Fun | Watches a configured player and warns CTs when they push a mapped zone; optional wall-glow and no-jump gag effects |
| **Inventory (ws)** | 🔗 Integration | `!ws` / `!wslogin` / `!loadout` — in-game skin sync via the [ianlucas inventory plugin](https://github.com/ianlucas/cs2-inventory-simulator-plugin) |

---

## Architecture

```text
GardenRetakes.sln
├── RetakesPlugin/            <- Main CSS plugin (loads at server start)
│   ├── Garden/
│   │   ├── GardenHost.cs     <- Module orchestrator
│   │   ├── GardenSettings.cs <- Merged config section (in RetakesPlugin.json)
│   │   └── Modules/
│   │       ├── AdminModule.cs
│   │       ├── AllocatorModule (Allocator/)
│   │       ├── DuelsModule.cs
│   │       ├── EditModeModule.cs
│   │       ├── ExecutesModule.cs
│   │       ├── FastStratModule.cs
│   │       ├── GConfigModule.cs
│   │       ├── GameModeModule.cs
│   │       ├── InstantDefuseModule.cs
│   │       ├── Rankings (Rankings/)
│   │       ├── SmallServerModule.cs
│   │       ├── SpawnEditorModule.cs
│   │       └── SpotlightModule.cs
│   ├── lang/                 <- Localisation (en.json, fr.json)
│   └── map_config/           <- Per-map spawn JSON files
├── RetakesPluginShared/      <- Shared interfaces / DTOs
├── GardenRankingsCore/       <- Rankings engine (ELO, rating, DB)
├── RetakesAllocatorCore/     <- Allocator engine
├── RetakesAllocatorShared/
└── *Test/                    <- Unit test projects
```

The companion website lives in a **separate repository**:
→ [evanhzg/garden-retakes-website](https://github.com/evanhzg/garden-retakes-website) — Next.js 14 + Prisma, reads the same MySQL database, deployed at [retakes.fr](https://retakes.fr).

---

## Prerequisites

| Requirement | Version |
|---|---|
| [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) | Latest |
| [Metamod:Source](https://www.sourcemm.net/downloads.php/?branch=master) | Latest (dev build) |
| MySQL / MariaDB | 8.0+ |
| .NET SDK | 8.0+ (build only) |

> **SQLite is not supported.** Garden Rankings requires MySQL for the website to connect.

---

## Installation

### From a Release

1. Download the latest release zip from the [Releases page](../../releases/latest).
2. Extract and upload the contents to your server under:
   ```text
   game/csgo/addons/counterstrikesharp/plugins/RetakesPlugin/
   ```
3. Point the plugin at your MySQL database (see [Configuration](#configuration)).
4. Start the server — CSS will load the plugin and create all database tables on first run.
5. The config file `RetakesPlugin.json` is generated in `configs/plugins/RetakesPlugin/` on first load. Edit it to configure Garden modules.

### Recommended directory layout after extraction

```text
addons/counterstrikesharp/plugins/RetakesPlugin/
├── RetakesPlugin.dll
├── RetakesPlugin.json          <- main config (auto-generated, then hand-edited)
├── config/
│   ├── config.json             <- allocator config
│   └── rankings.json           <- rankings config
├── garden_admins.json          <- bootstrap admin fallback store
├── lang/
│   ├── en.json
│   └── fr.json
└── map_config/
    └── *.json                  <- per-map spawn files
```

---

## Configuration

### `RetakesPlugin.json` — `GardenSettings` section

The `GardenSettings` key is merged into the standard CSS config. Each module has an `Enabled` flag.

```jsonc
{
  // ... standard retakes settings ...
  "GardenSettings": {
    "Admin": {
      "OwnerSteamIds": [],         // SteamID64s that are always Owner-level (bootstrap)
      "EnableShortAliases": false  // Enable !kick, !slay etc. once legacy plugins are retired
    },
    "Allocator":   { "Enabled": true },
    "Rankings":    { "Enabled": true },
    "InstantDefuse": {
      "Enabled": true,
      "BlockOnUtilityDanger": true  // Don't insta-defuse while HE/molotov is active
    },
    "SmallServer": {
      "Mode": "Auto",               // "Auto" | "On" | "Off"
      "MaxHumans": 3,
      "MaxTeamNades": 2,
      "UseFlaggedSpawns": true,
      "InstantRoundSwitchOnLastCtDeath": true
    },
    "Duels": {
      "Enabled": true,
      "MaxPairDistance": 1500,
      "MaxParallelDuels": 3,
      "SpectatorAutoFollow": true,
      "Weapons": ["weapon_ak47", "weapon_deagle"],
      "GiveKevlarHelmet": true
    },
    "Executes": {
      "Enabled": true,
      "TWeapons": ["weapon_ak47", "weapon_deagle"],
      "CtWeapons": ["weapon_m4a1", "weapon_deagle"],
      "GiveKevlarHelmet": true
    },
    "FastStrat": { "Enabled": false },
    "Spotlight": {
      "Enabled": true,
      "Targets": [],                // SteamID64s to watch
      "Alias": "Damien",
      "AlertWindowSeconds": 15,
      "AlertAudience": "CT",        // "CT" | "T" | "all"
      "AlertOnlyWhenT": true,
      "AutoReveal": false,
      "AutoNoJump": false
    }
  }
}
```

### `config/rankings.json`

Controls ranked mode thresholds, ELO gain/loss, CR match settings, map aliases, and more. Loaded and hot-reloadable with `!rankings_reload_config`.

### `config/config.json` (allocator)

Weapon preferences, AWP rules, buy logic, grenade allocation. Hot-reloadable with `!reload_allocator_config`.

### Database

Set `DatabaseProvider` to `"MySql"` and provide a connection string. The plugin creates all tables on first start:

```json
"DatabaseProvider": "MySql",
"DatabaseConnectionString": "Server=your-db-host;Port=3306;Database=gardenrankings;Uid=user;Pwd=password;"
```

---

## Commands

Full command reference: **[COMMANDS.md](COMMANDS.md)**

### Quick reference

| Category | Key commands |
|---|---|
| **Weapons** | `!guns`, `!menu`, `!awp`, `!zeus`, `!gun <name>` |
| **Rankings** | `!elo`, `!stats`, `!top`, `!rr` (ranked vote), `!cr` (competitive retakes) |
| **Inventory** | `!ws`, `!wslogin`, `!loadout [name]`, `!borrow <key>` |
| **Game mode** | `!gamemode [mode]`, `!gedit` (editor menu) |
| **Admin** | `!gadmin add/remove/list`, `!gkick`, `!gslay`, `!gmap`, `!grcon` |
| **Spawn editor** | `!gspawns`, `!gspawn add/del/move/flag/info/test` |
| **Config** | `!gconfig [target] [path] [value]` |
| **Seasons** | `!season_new`, `!seasons` |

---

## Game Modes

Switch with `!gamemode <mode>` (Admin level):

| Mode | Description |
|---|---|
| `retakes` | Default retakes loop |
| `duels` | 1v1 arena duels in named lanes |
| `executes` | T-side execute strategies with recorded nade lineups |
| `faststrat` | Players vote on strategy/setup each round |
| `edit` | Admin editor — place spawns, create arenas, record lineups |

---

## Building from Source

```bash
# Clone
git clone https://github.com/evanhzg/garden-retakes.git
cd garden-retakes

# Build all projects
dotnet build GardenRetakes.sln

# Run tests
dotnet test

# Output (upload this folder to your server)
# RetakesPlugin/bin/Debug/net8.0/
```

> Make sure your `NuGet.config` points at the correct CounterStrikeSharp feed. See `NuGet.config` in the root.

---

## Credits & Upstream

- **[b3none/cs2-retakes](https://github.com/b3none/cs2-retakes)** — The original CS2 retakes plugin this project is forked from. Core queue/team/spawn logic is inherited from there.
- **[splewis/csgo-retakes](https://github.com/splewis/csgo-retakes)** — The original CS:GO retakes concept.
- **[ianlucas/cs2-inventory-simulator-plugin](https://github.com/ianlucas/cs2-inventory-simulator-plugin)** — The in-game skin equip plugin that `!ws` integrates with.
- **[@ianlucas/cs2-lib](https://github.com/ianlucas/cs2-lib)** — Weapon/skin/sticker catalog used by the inventory system.
- **[roflmuffin/CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)** — The C# framework for CS2 server plugins.

---

## License

This project is licensed under the **GNU General Public License v3.0** — the same license as the upstream `b3none/cs2-retakes` it is forked from.

See [LICENSE](LICENSE) for the full text.

**What this means in practice:**
- ✅ You can use, study, and run this on your own server freely.
- ✅ You can fork and modify it.
- ✅ If you distribute a modified version (e.g. publish your own fork), you **must** release your changes under GPL-3.0 as well.
- ❌ You cannot re-license it under a proprietary or closed-source license.

> If you're thinking of building something on top of this, GPL-3.0 is fine for server-side plugins — it does not affect what runs on player clients. Just keep your fork public if you share it.
