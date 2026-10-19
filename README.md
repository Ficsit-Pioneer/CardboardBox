<p align="center"><img src="tools/logo-256.png" width="128" alt="CardboardBox logo"></p>

<h1 align="center">CardboardBox</h1>

<p align="center"><b>The simplest way to run a Minecraft server on Windows.</b><br>
One small <code>.exe</code>. No installs, no command line, no port-forwarding guides (well, mostly).</p>

---

## For the person running the server

1. Download **`CardboardBox.exe`** from the [latest release](../../releases/latest).
2. Double-click it. If Windows says *"Windows protected your PC"*, click **More info**, then **Run anyway**.
   (This shows up for any new program that isn't from a big company.)
3. Wait for it to finish setting up (the first time it downloads Java and Minecraft, about 120 MB).
4. Press **Start server** and agree to the Minecraft EULA. If Windows asks about
   **"OpenJDK Platform binary"**, click **Allow** (that's the Minecraft server; friends can't connect without it). Done!

Friends on the same Wi-Fi join using the **Home network address** shown on the Dashboard.
Players need the same Minecraft version as the server (shown on the Dashboard).

### Letting friends join from anywhere (playit.gg, easiest)

On the Dashboard, click **Use playit.gg (easiest)** under *Internet address*. Your browser opens
[playit.gg](https://playit.gg): log in or make a free account, then approve CardboardBox. That's it.
CardboardBox creates a Minecraft tunnel for you and shows an address like `some-name.tun.ply.gg`. Send that to friends
(just the name, no port needed). No router setup is needed, and the tunnel runs automatically whenever the server runs.
You can switch it off, set it up again or disconnect it on the **Server Settings** page.

Prefer not to use playit.gg? Try the **Auto port-forward** link instead, or forward port `25565` (TCP)
to this PC in your router settings. Friends then use the internet address shown on the Dashboard.

### Choosing the server type, Minecraft version and crossplay

**Server Settings → Server type** lets you pick, with one click on **Apply server type**:

| Software | Good for |
|---|---|
| **Paper** (default) | Plugins. The fastest and most popular choice. Players join with the normal game. |
| **Purpur** | Paper plus lots of extra gameplay options. Same plugins. |
| **Fabric** | Lightweight mods. Fabric API is added for you. |
| **NeoForge** | Big mods and modpacks (the modern Forge). |
| **Forge** | The classic mod loader. |
| **Vanilla** | Plain Minecraft from Mojang. |

- **Minecraft version**: any version that software supports, or *Latest*. CardboardBox picks and downloads the right Java automatically.
- **Crossplay**: tick *Let Bedrock players join too* (Paper, Purpur, Fabric or NeoForge) and phones, tablets, consoles and
  Windows Bedrock players can join. CardboardBox installs [Geyser](https://geysermc.org) and Floodgate
  (plus ViaVersion when needed), so Bedrock players don't need a Java account. They add a server with your address and port **19132**.
- Before switching, CardboardBox **backs up the world**. Minecraft can't move a world back to an older version,
  so CardboardBox blocks that and suggests starting a new world instead.
- Paper/Purpur and Fabric/NeoForge/Forge/Vanilla can't open each other's worlds, so **each side keeps its own world**.
  Switching across puts the current world aside, and switching back brings it back.

### What's in the app

| Page | What it does |
|---|---|
| **Dashboard** | Start / Stop / Restart, players online, CPU and RAM (with live charts), uptime, server type and version, server addresses (including playit.gg and Bedrock), GitHub sync status, a message from the server owner |
| **Console** | Live server log, a box to type commands, and quick buttons (who's online, save, TPS, plugins) |
| **Server Settings** | Server type / Minecraft version / crossplay, server message (MOTD) with a live preview, server icon, max players, RAM, difficulty, game mode, port, whitelist, world seed / type / hardcore, *start a new world*, playit.gg, the server owner's admin panel (on/off), and auto-restart / backup options |
| **Plugins & Mods** | Everything installed (plugins or mods, whichever your server type uses), **search Modrinth and install with one click** (required add-ons come along), add `.jar` files, turn things on/off, upload to GitHub |
| **Players** | Online / operators / whitelist / banned lists, plus one-click op, kick, ban, whitelist |
| **Backups** | Back up the world (even while it's running), restore, and keep the newest *N* backups |
| **GitHub** | Sync status, check now, change the check interval, optional upload token, app updates |

### It looks after itself

- **Installs everything.** Java (Eclipse Temurin), the server software, plugins and mods, checking every download against its checksum when one is published.
- **Checks GitHub every hour.** If this repo changed, it downloads the new files. If the server is running, it warns players in-game, makes a backup and restarts (you can turn this off).
- **Crash recovery.** If the server crashes it restarts automatically, up to 3 times in 10 minutes.
- **Plain-English errors.** Common problems (port already in use, not enough RAM, wrong Java, a broken plugin, no internet) are explained on screen.
- **Works offline.** If GitHub can't be reached, it starts with the files from last time.
- **playit.gg tunnels.** One browser approval, then CardboardBox creates the tunnel, downloads playit's official signed agent (checksum-verified) and runs it alongside the server.
- **Never leaves a zombie server.** If CardboardBox is closed or crashes, Java is shut down with it, and the PC won't go to sleep while the server is running.
- **Updates itself.** When a new release is published here, the app offers a one-click update.

---

## For the server owner (you, editing this repo)

This repo is the **source of truth**. Every CardboardBox that points at it copies these files:

```
cardboardbox.json        <- what to install (server type, Minecraft version, crossplay, plugins/mods, RAM, announcement)
server/                  <- copied into the server folder on every PC
  server.properties      <- base settings (merged key by key, see below)
  server-icon.png        <- 64x64 server icon
  plugins/               <- plugin .jar files (Paper/Purpur) and their config folders
  mods/                  <- mod .jar files (Fabric/NeoForge/Forge)
  config/                <- mod config files
  ...anything else       <- bukkit.yml, paper configs, datapacks, etc.
app/                     <- the CardboardBox source code (C#)
hub/                     <- your admin panel + relay (a Cloudflare Worker), see "Your admin panel"
```

Edit files on github.com (or push with git). Within an hour every server picks up the change.
To push a change immediately, your friend can press **Check GitHub now**.

### `cardboardbox.json`

```jsonc
{
  "serverName": "CardboardBox",        // name shown in the app's MOTD preview
  "software": "paper",                 // paper, purpur, fabric, neoforge, forge or vanilla
  "minecraftVersion": "26.2",          // e.g. 26.2, or "latest"
  "loaderVersion": "",                 // empty = newest stable Paper/Purpur build, Fabric loader, NeoForge or Forge version
  "crossplay": false,                  // true = Bedrock players can join too (Geyser + Floodgate)
  "adminHub": "https://...workers.dev", // your admin panel (see below); leave out for none
  "javaVersion": 0,                    // 0 = automatic (what that Minecraft version needs)
  "defaultRamGB": 4,                   // capped to what the PC can spare
  "announcement": "Hi!",               // shown on everyone's Dashboard
  "extraJvmFlags": [],                 // extra Java flags, if you know you need them
  "plugins": [                         // plugins or mods ("mods" works as a name for this list too)
    "luckperms",                                  // Modrinth project name: the right version is picked automatically
    { "modrinth": "chunky" },                     // same thing, long form
    { "url": "https://example.com/Thing.jar",     // any direct download link
      "file": "Thing.jar", "sha256": "optional" }
  ]
}
```

Names in this list are **Modrinth project IDs** (no spaces): the last part of the plugin's Modrinth page address.
For example "Donut Homes" lives at `modrinth.com/plugin/donutsmp-homes`, so you'd add `"donutsmp-homes"`.
Easier: install it from the **Plugins & Mods** page in the app with *Also add it to the GitHub list* ticked, and the
right ID is written here for you.

Anything listed here is downloaded by each PC (with its required dependencies), so you don't need to commit big `.jar` files.
If something has no version for your server type and Minecraft version, it's skipped with a warning and the server still starts.
The list is for the `software` set in this file: Paper plugins also work on Purpur, but if a PC switches itself to a
mod loader (or the other way round), the list isn't used there - add-ons for that PC come from its Plugins & Mods page.
Remove an entry from the list and it's removed from every server.

Settings chosen in the app (server type, Server Settings values) apply to that PC only, until someone with a token presses
**Save & upload to GitHub**. Then they're written into this repo for everyone.

### How syncing works (the important rules)

- A file that's in the repo but missing on the PC is **copied in**.
- A file you **changed** in the repo **overwrites** the PC's copy.
- A file you **didn't** change in the repo is **left alone**, so plugins and mods can keep editing their own configs.
- A file you **delete** from the repo is **deleted** on every PC.
- `server.properties` is merged **key by key**: your keys win, Minecraft keeps the rest.
  Settings the friend changes in the app are layered on top (shown as "differ from GitHub" in the app,
  with a button to reset them).
- Worlds, `eula.txt`, player lists and dot-files are **never** touched by syncing.

### Letting the friend upload changes (optional)

Downloading never needs an account. If you want the friend to be able to upload settings, plugins or mods
from the app, make a [fine-grained token](https://github.com/settings/personal-access-tokens/new)
limited to this repo with **Contents: Read and write**, and paste it on the app's GitHub page.
It's stored encrypted with Windows' DPAPI for that Windows user only.

### Your admin panel (manage every server from one page)

Open **https://cardboardbox-hub.william-h-morin.workers.dev** to see every CardboardBox that uses this repo:
whether it's running, players, CPU / RAM, uptime, how players join, and a live console. From there you can
**start, stop, restart, run any Minecraft command, back up the world and make it check GitHub**, without
needing access to anyone's PC.

**First time:** set your admin password (only you know it). In a terminal in this repo:

```powershell
cd hub
npm install
npx wrangler secret put ADMIN_KEY
```

Type a long password when asked, then sign in on the panel with it. Run the last command again any time to change it.

How it works and what your friends see:

- Each CardboardBox connects *out* to the hub (`hub/`, a free Cloudflare Worker on your account), so nobody needs
  port forwarding. Servers send their status every minute, or every 5 seconds while you have the panel open.
- The hub's address is `"adminHub"` in `cardboardbox.json`. Remove it to switch the panel off for everyone.
- On a friend's PC the sidebar shows **Owner's admin panel: connected**, every action you take appears in their
  Console, and **Server Settings → Server owner's admin panel** lets them switch it off or change the name you see.
- The panel can only control the Minecraft server. It can't see or change anything else on anyone's PC.
- To update the hub after changing `hub/`: `cd hub`, then `npx wrangler deploy`.

### Releasing a new version of the app

```powershell
git tag v1.2.1
git push origin v1.2.1
```

GitHub Actions builds `CardboardBox.exe` and attaches it to a release. Every running copy then offers the update.
To build locally instead: `powershell -ExecutionPolicy Bypass -File build.ps1` (output in `dist\`).
It needs the .NET SDK. The exe itself targets .NET Framework 4.8, which comes with every Windows 10/11 PC.

---

## Bedrock crossplay details

Bedrock Edition switches from its old connection method (RakNet) to **NetherNet** on October 27, 2026 (Bedrock 26.60).
CardboardBox sets Geyser up for both, so old and new Bedrock versions can join:

| Port | Protocol | What for |
|---|---|---|
| 19132 | UDP | Bedrock before 26.60 (RakNet) |
| 19132 | TCP | NetherNet signaling (how new Bedrock finds the server) |
| 19133 | UDP | NetherNet game traffic |

On the same Wi-Fi this just works. For Bedrock players outside the home, Geyser also registers the server with
GeyserMC's signaling service ("assisted joins"), which helps players reach a server behind a router. If that isn't
enough, forward the three ports above to this PC. playit.gg's Bedrock tunnel only covers the old RakNet method.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| "Port 25565 is already in use" | Another server is running. Close it, check Task Manager for `java.exe`, or change the port in Server Settings. |
| Friends outside the house can't join | Use playit.gg (Dashboard → *Use playit.gg*). Or try **Auto port-forward**, or forward TCP 25565 in the router. |
| A friend gets "Outdated client" / "Outdated server" | Their game version doesn't match the server. Pick that version in their Minecraft Launcher (Installations). |
| "Minecraft can't move a world back to ..." | Worlds can only go to the same or a newer version. Pick that version, or use **Start a new world** in Server Settings. |
| A mod or plugin won't install | It may not exist yet for your server type and version. The Console page says which one; the server starts without it. |
| playit.gg says the key was rejected | The agent was removed on playit.gg. On **Server Settings**, press **Set up again...**. |
| Server crashes with "ran out of memory" | Raise RAM in Server Settings, or remove heavy plugins/mods. |
| Something else | Open the **Console** page. The full log is in `C:\Users\<you>\CardboardBox\logs`. |

Advanced: set the environment variable `CARDBOARDBOX_HOME` to keep CardboardBox's files somewhere else.

*Minecraft is a trademark of Mojang AB. CardboardBox is not affiliated with Mojang or Microsoft.
Server software comes from [PaperMC](https://papermc.io), [PurpurMC](https://purpurmc.org), [Fabric](https://fabricmc.net),
[NeoForged](https://neoforged.net), [Forge](https://minecraftforge.net) and Mojang; crossplay from [GeyserMC](https://geysermc.org);
add-ons from [Modrinth](https://modrinth.com); Java builds from [Eclipse Adoptium](https://adoptium.net).*
