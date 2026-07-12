# Garden Retakes Roadmap

This document outlines planned features and improvements for the Garden Retakes plugin ecosystem, covering the game server, website, and database integrations.

## Priority Inbox

### 1. Discord & Web Integration
- `[x]` **Discord Webhooks for Big Plays**: Send automated messages to a designated Discord channel for significant events (e.g., 1v4 clutches, Peak ELO broken, CR match results).
- `[x]` **Web Profile "Heatmaps" & Advanced Stats**: Add a dedicated "stats" page with a "maps" tab on the website to display performance heatmaps (e.g., kill/death locations) and advanced metrics. Filterable by player, average, etc.
- `[x]` **Live Match Spectator Page**: Create a live web dashboard showing real-time scoreboard data and ELO predictions for ongoing ranked/CR matches.

### 2. In-Game QoL & Polish
- `[x]` **Custom Chat Tags / Colors**: Assign dynamic chat prefixes and colors based on a player's ELO bracket, with special exclusive tags for the Top 3 players on the ladder.
- `[x]` **Warmup Deathmatch/Practice for CR**: Instead of a stagnant pause during Competitive Retakes setup, allow instant-respawn or infinite ammo practice until the match officially starts.
- `[x]` **"Drop Bomb" Priority**: Allow players to opt-out of carrying the C4. *Exception*: If every Terrorist opts out or if a player is solo, they are forced to take it.
- `[x]` **Dynamic MVP / Hype Announcements**: Replace standard end-of-round text with dynamic center-screen messages celebrating specific achievements (e.g., "EVAN CLUTCHED 1v3").
- `[x]` **"Rivalry" / Nemesis Notifications**: Track head-to-head records within a single match/session. Announce dominations (e.g., "A is dominating B (3-0)"). Surface these head-to-head records in the website comparison tool.

### 3. Gameplay & "Hype" Features
- `[x]` **Retake "Scenarios"**: Add a toggleable setting (off by default) to force specific situational setups (e.g., "Post-plant 2v3 with low health") rather than fully random spawns. Modify the spawn editor tool to accommodate these specific scenario tags.

### 4. Admin / Host QoL
- `[x]` **Pause / Timeout Feature (`!pause` or `!p`)**: Allow teams to pause a live CR match. The match stays paused until the pausing team types `!up` or `!unpause`.
- `[x]` **Ghost Mode / Freecam for Admins**: Add a command that puts admins into an invisible freecam mode without occupying a player slot, for moderation and spawn creation.

### 5. Future Analytics
- [ ] **Positional Coordinate Heatmaps**: Update the CS2 plugin to track precise X/Y/Z coordinates of kills/deaths using pawn placement data, and build a dedicated minimap UI on the website to visualize player positional performance.
