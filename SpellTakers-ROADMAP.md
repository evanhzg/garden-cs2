# The SpellTakers Project Roadmap

This roadmap breaks down the entire ARAM/SpellTakers concept into sequential, technical phases. It defines the exact architecture required to bridge Counter-Strike 2, real-time web technologies, and custom desktop overlays, along with a dedicated verification pipeline for step-by-step testing at each stage.

---

## [x] Phase 1: Core Architecture & Backend Infrastructure
**Goal:** Establish the server environment, real-time communication pipeline, and database foundation.

### Technical Breakdown
*   **Server Foundation:** Install Metamod and CounterStrikeSharp on the CS2 server. This is the bedrock for all C# server-side logic.
*   **Real-Time Data Pipeline:** Integrate `websocket-sharp` into your CounterStrikeSharp plugin. This allows the game server to broadcast live events (health updates, cooldowns, kills) as JSON payloads to external clients.
*   **Database (Firebase):** Set up Firestore to track player stats, win rates, and class selections. Link this with Firebase Authentication to map Steam IDs to user profiles.

### Step-by-Step Testing & Verification
1.  **Server Baseline:** Boot the server and type `meta list` and `css plugins` in the server console. Verify both systems return a successful status without errors.
2.  **WebSocket Ping Test:** Run a simple local Python or JavaScript script to connect to the server's WebSocket port (e.g., `ws://your-server-ip:8080`). Fire a manual test JSON payload from the server console and verify the local script receives it instantly.
3.  **Database Write Test:** Force a mock player connection event on the server. Verify that a new document matching the player's SteamID is successfully created in your Firebase Firestore collection.

> **AI Prompting Strategy (Opus / Gemini):**
> *"Write a CounterStrikeSharp plugin class that initializes a WebSocket server on port 8080 using `websocket-sharp`. Create a method that takes a custom C# object (containing EventType, PlayerSteamID, and Value), serializes it to JSON, and broadcasts it to all connected WebSocket clients."*

---

## [ ] Phase 2: Map Engineering (Source 2 Tools & Hammer)
**Goal:** Build the physical lanes, objective zones, and tower mechanics.

### Technical Breakdown
*   **Lane Segmentation:** Use invisible `clip` brushes or `trigger_hurt` kill-zones to block off standard maps, creating single-lane experiences (e.g., locking players exclusively into Inferno's Banana).
*   **The Tower Entity:** Place a `prop_dynamic` for the tower model. Give it a targetname (e.g., `lane_tower_01`) and configure its health properties.
*   **The Vulnerability Zone:** Draw a `trigger_multiple` around the tower base. Use a `math_counter` to track the number of defending players inside the trigger. When the count is greater than zero, apply a `filter_damage_type` to make the tower immune. When the count hits zero, remove the filter.

### Step-by-Step Testing & Verification
1.  **Boundary Check:** Compile the map and load it locally. Walk towards the blocked off zones. Verify that your player model cannot physically bypass the clip brushes, or is instantly killed by the `trigger_hurt` zones.
2.  **Zone Count Test:** Turn on developer developer overlays (`developer 1` and `ent_messages_draw 1`). Step a player character into the tower's `trigger_multiple` zone. Verify via console logs that the `math_counter` increments to 1 and fires the output to enable the damage filter on the tower.
3.  **Vulnerability Toggle:** Stand inside the zone and shoot the tower; verify it takes 0 damage. Have a second player or bot step out of the zone (counter hits 0). Shoot the tower again; verify that it now shatters or registers health reduction.

> **AI Prompting Strategy (Fable 5):**
> *"Provide step-by-step instructions for the CS2 Hammer Editor to create a dynamic tower objective. Specifically, detail the Entity I/O logic required to connect a `trigger_multiple`'s `OnStartTouch` and `OnEndTouch` events to a `math_counter`, and how that counter can toggle a `filter_damage_type` entity applied to a `prop_dynamic`."*

---

## [ ] Phase 3: Server-Side Game Logic (CounterStrikeSharp)
**Goal:** Program the MOBA mechanics, class constraints, and custom damage calculations.

### Technical Breakdown
*   **Drafting/Veto System:** Use the native CS2 Panorama Vote UI or a chat-command menu to let players select the "ARAM" or "SpellTakers" mode, and subsequently draft their classes (Juggernaut, Assassin, Caster).
*   **jRandomSkills Integration:** Tie specific skill flags (vampirism, gravity, speed) to the drafted classes. For ARAM, randomize these assignments on every player spawn.
*   **Ranged vs. Melee Logic:** Hook the `OnTakeDamage` event. Measure the 3D distance between the attacker and the tower. If the attacker is further than 150 units, nullify the damage unless they are flagged with the Ranged class modifier.

### Step-by-Step Testing & Verification
1.  **Draft Menu Test:** Join the server and type the draft command (or trigger the vote). Verify that clicking the menu options successfully assigns your player variable to the chosen class (`Class = Assassin`).
2.  **Passive Modifier Test:** Select the "Assassin" class, spawn, and check your movement speed and gravity via server commands or player feel. Verify that your skills from `jRandomSkills` trigger correctly on keypress.
3.  **Distance Damage Test:** Select a non-ranged class. Stand right next to the tower and knife it; verify the damage registers. Take 5 steps back and shoot it with a pistol; verify that the console outputs a "Damage Nullified: Too Far" log and the tower takes 0 damage. Switch to the Ranged class, stand far away, shoot it, and verify the damage goes through.

> **AI Prompting Strategy (Opus 4.8):**
> *"Write a CounterStrikeSharp `OnTakeDamage` hook for a custom CS2 mode. Given a `CEntityInstance` representing a tower and a `CCSPlayerController` representing the attacker: calculate the 3D vector distance between them. If the distance exceeds 150 units, instantly nullify the damage, UNLESS the attacker's controller has a custom boolean `IsRangedClass` set to true."*

---

## [ ] Phase 4: Custom Asset Pipeline
**Goal:** Deliver custom models (Hammers, Sceptres, custom player skins) to the players seamlessly.

### Technical Breakdown
*   **Compilation:** Import custom `.fbx` or `.obj` assets into CS2 Workshop Tools. Rig melee weapons to the default knife skeleton animations and compile them into `.vmdl_c` files. Pack these into a Steam Workshop VPK.
*   **Client Distribution:** Configure `MultiAddonManager` with your Workshop ID to force clients to download the asset packs upon connecting to the server.
*   **Dynamic Equipping:** Intercept the player spawn event. Strip default weapons, grant a knife, and programmatically swap the knife's active `CBaseEntity` model path to your custom `.vmdl_c` path.

### Step-by-Step Testing & Verification
1.  **Download Verification:** Clear your local CS2 client cache, then connect to your server. Verify that the loading screen forces a download of your custom workshop addon before allowing entry.
2.  **Model Swap Verification:** Spawn into the game as a Juggernaut. Look down at your hands in first-person, and have another player look at you in third-person. Verify that the default knife has been replaced visually by the custom War Hammer model.
3.  **Animation Check:** Perform a left-click swing and a right-click stab with the custom melee weapon. Ensure the model follows the hand animations smoothly without tearing or floating away from the player's hands.

> **AI Prompting Strategy (Fable 5 / Opus):**
> *"In CounterStrikeSharp, how do I hook the player spawn event to strip a player's weapons, give them a knife, and then change the knife's world model and view model paths to a custom `.vmdl_c` file downloaded via MultiAddonManager?"*

---

## [ ] Phase 5: The Transparent Desktop Overlay (Tauri + Next.js)
**Goal:** Bypass CS2's strict Panorama UI limitations to display custom MOBA hotbars and tower health.

### Technical Breakdown
*   **Application Shell:** Build a lightweight desktop client using Tauri. Configure the window properties in Rust to be completely frameless, transparent, always-on-top, and pass-through (so mouse clicks pass directly into the game window beneath it).
*   **Frontend UI:** Build the layout (cooldown wheels, ability icons, top-centered health bars) in Next.js.
*   **Live Synchronization:** Establish a WebSocket client connection within the Next.js app to listen for the payloads sent by Phase 1's CS2 server.

### Step-by-Step Testing & Verification
1.  **Overlay Overlaying Test:** Launch CS2 in "Borderless Windowed" mode. Launch your Tauri application. Verify that the app window stays visible on top of the game, even when you click inside CS2 to look around and shoot.
2.  **Click-Through Verification:** Attempt to engage in a normal gunfight in CS2 while the overlay app is running. Verify that crosshair movement and primary clicks are completely unhindered by the floating UI.
3.  **Live State Update Test:** While in-game, hit your ability key (e.g., 'Q' for Dash). Verify that the server sends the WebSocket packet, the overlay receives it, and the UI immediately renders a greyed-out cooldown countdown clock over the icon in real time.

> **AI Prompting Strategy (Gemini Pro):**
> *"Provide the `tauri.conf.json` configuration and the necessary Rust window initialization code to create a completely transparent, borderless, always-on-top window that ignores mouse clicks (pass-through). Then, provide a Next.js React component that connects to a local WebSocket, listens for 'cooldown_update' events, and animates a CSS ability hotbar."*

---

## [ ] Phase 6: Web Dashboard Integration (`retakes.fr`)
**Goal:** Provide out-of-game progression, automated statistics tracking, and configuration sync.

### Technical Breakdown
*   **Database Syncing:** Ensure the game server reliably pushes end-of-match data summaries (victory/defeat, damage dealt to towers, skills used) to Firestore.
*   **Web Development:** Expand your existing Next.js infrastructure on `retakes.fr` to render SpellTakers leaderboards, match histories, and class pick/ban statistics.
*   **Workflow Automation:** Utilize tools like Make or Apify to automate backend data aggregation, update server config files remotely from a web panel, or post match results to Discord.

### Step-by-Step Testing & Verification
1.  **End-Match Payload Test:** Intentionally trigger a game-ending condition on the server (destroy a final tower). Check your Firebase Console to confirm a complete match log document generates within 5 seconds.
2.  **Dashboard Rendering Test:** Navigate to the development route on `retakes.fr`. Refresh the page and verify the match you just completed shows up under the "Recent Matches" tab with correct information.
3.  **Automation Webhook Test:** Complete a test game that alters the top player on the leaderboard. Verify that your automation service fires a Discord webhook or updates the active live-server variables cleanly without manual intervention.

---

## [ ] Phase 7: Web Lobby & Matchmaking (retakes.fr)
**Goal:** Create a real-time web lobby for players to gather, veto maps, draft classes, and launch into the server.

### Technical Breakdown
*   **Lobby State:** Create a real-time lobby system (using WebSocket or Server-Sent Events in Next.js) where players can join a queue.
*   **Veto & Draft UI:** Build an aesthetic UI for Map Vetoes and Class Selection (Juggernaut, Assassin, Caster).
*   **Server Dispatch:** Once the lobby is full and vetoes are complete, configure the server remotely (via RCON or API) to lock the teams, set the gamemode, and provide players with the `steam://connect/` link.