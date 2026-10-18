using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CardboardBox.Core
{
    /// <summary>Server type (Paper, Fabric, ...), Minecraft version, crossplay and the world's version.</summary>
    public partial class AppController
    {
        public string EffectiveSoftware => Settings.SoftwareOverride ?? Manifest.Software ?? "paper";
        public string EffectiveVersion => Settings.VersionOverride ?? Manifest.MinecraftVersion ?? "latest";
        public bool EffectiveCrossplay => Settings.CrossplayOverride ?? Manifest.Crossplay;

        /// <summary>The chosen server software (falls back to Paper if the name is unknown).</summary>
        public ServerSoftware Software
        {
            get
            {
                try { return ServerSoftware.Get(EffectiveSoftware); }
                catch (FriendlyException) { return ServerSoftware.Get("paper"); }
            }
        }

        /// <summary>The software that's actually installed right now (may differ until the next start).</summary>
        public ServerSoftware InstalledSoftware
        {
            get
            {
                try { return State.InstalledSoftware == null ? null : ServerSoftware.Get(State.InstalledSoftware); }
                catch (FriendlyException) { return null; }
            }
        }

        public bool CrossplayActive => EffectiveCrossplay && Software.SupportsCrossplay;

        /// <summary>Installs or updates the chosen server software (called from PrepareAsync).</summary>
        private async Task InstallSoftwareAsync()
        {
            ServerSoftware sw;
            try { sw = ServerSoftware.Get(EffectiveSoftware); }
            catch (FriendlyException ex) { throw new FriendlyException("cardboardbox.json asks for " + ex.Message, ex.Hint, ex); }

            SetBusy("Checking for " + sw.Name + " updates...");
            SoftwareBuild build = null;
            try
            {
                build = await sw.ResolveAsync(EffectiveVersion, Manifest.LoaderVersion);
            }
            catch (Exception ex)
            {
                bool usable = State.InstalledSoftware == sw.Id && LaunchFilesPresent() && State.JavaMinimum > 0;
                if (!usable || ex is FriendlyException && !(ex.InnerException is System.Net.Http.HttpRequestException) && Errors.Friendly(ex).Contains("isn't available"))
                    throw;
                Log.Warn("Couldn't check for " + sw.Name + " updates, using the installed version: " + Errors.Friendly(ex));
            }

            if (build != null && (build.Key != State.InstalledKey || !LaunchFilesPresent()))
            {
                GuardWorldVersion(build.Software, build.McVersion);

                bool switching = State.InstalledKey != null &&
                                 (State.InstalledSoftware != build.Software || State.InstalledMcVersion != build.McVersion);
                if (switching && Backups.WorldFolders().Count > 0 && !Server.IsAlive)
                {
                    SetBusy("Backing up the world before switching...");
                    await Backups.CreateAsync(null, "before-" + build.Software + "-" + build.McVersion, Settings.KeepBackups);
                }
                var fromFamily = FamilyOf(State.InstalledSoftware);
                var toFamily = FamilyOf(build.Software);
                if (State.InstalledSoftware != null && fromFamily != toFamily && !Server.IsAlive)
                    SwapWorlds(fromFamily, toFamily);
                if (State.InstalledSoftware != build.Software || State.InstalledMcVersion != build.McVersion)
                    CleanSoftwareFiles();

                int javaMajor = Manifest.JavaVersion > 0 ? Manifest.JavaVersion : build.JavaMajor;
                SetBusy("Checking Java " + Installer.PickJavaFeature(javaMajor) + "...");
                _javaExe = await Installer.EnsureJavaAsync(Installer.PickJavaFeature(javaMajor), Progress);

                Log.Info("Installing " + sw.Name + " for Minecraft " + build.McVersion + (build.Experimental ? " (experimental build)" : "") + "...");
                SetBusy("Installing " + sw.Name + " " + build.McVersion + "...");
                State.LaunchArgs = await sw.InstallAsync(build, _javaExe, Progress);
                State.InstalledSoftware = build.Software;
                State.InstalledMcVersion = build.McVersion;
                State.InstalledLoader = build.Loader;
                State.InstalledKey = build.Key;
                State.JavaMinimum = javaMajor;
                State.JavaFlags = build.JvmFlags.Count > 0 ? build.JvmFlags : ServerSoftware.DefaultJvmFlags.ToList();
                State.Save();
                Log.Success("Installed " + DescribeInstalled() + ".");
            }

            int feature = Installer.PickJavaFeature(State.JavaMinimum > 0 ? State.JavaMinimum : 21);
            SetBusy("Checking Java " + feature + "...");
            _javaExe = await Installer.EnsureJavaAsync(feature, Progress);

            SetBusy("Checking " + sw.AddonWord + "...");
            await Installer.EnsureAddonsAsync(sw, State.InstalledMcVersion, await AddonSpecsAsync(sw), State, Progress);
            if (CrossplayActive) GeyserConfig.Apply(sw);
            RepoSync.ApplyDisabledPlugins(Settings);
            State.Save();
        }

        /// <summary>Add-ons to install: the cardboardbox.json list plus anything the setup needs.</summary>
        private async Task<List<PluginSpec>> AddonSpecsAsync(ServerSoftware sw)
        {
            // The list in cardboardbox.json is written for the server type chosen on GitHub. Paper plugins
            // also work on Purpur, but they don't turn into working Fabric/Forge mods (and vice versa).
            ServerSoftware listFor = null;
            try { listFor = ServerSoftware.Get(Manifest.Software); } catch (FriendlyException) { }
            bool listFits = listFor == null || listFor.Id == sw.Id || (listFor.AddonFolder == "plugins" && sw.AddonFolder == "plugins");
            var specs = listFits ? new List<PluginSpec>(Manifest.Plugins) : new List<PluginSpec>();
            if (!listFits && Manifest.Plugins.Count > 0)
                Log.Info("The " + Manifest.Plugins.Count + " add-on(s) listed on GitHub are " + listFor.AddonWord + " for " + listFor.Name +
                         ", so they aren't used on " + sw.Name +
                         (sw.AddonFolder == null ? " (it can't use plugins or mods)." : ". Add " + sw.AddonWord + " on the Plugins & Mods page instead."));
            bool Listed(string slug) => specs.Any(s => string.Equals(s.Modrinth, slug, StringComparison.OrdinalIgnoreCase));

            if (sw.Id == "fabric" && !Listed("fabric-api"))
                specs.Insert(0, new PluginSpec { Modrinth = "fabric-api", IsDependency = true }); // nearly every Fabric mod (and Geyser) needs it

            if (EffectiveCrossplay && !sw.SupportsCrossplay)
                Log.Warn("Crossplay (Bedrock players) isn't available on " + sw.Name + ". Use Paper, Purpur, Fabric or NeoForge for it.");
            else if (EffectiveCrossplay && sw.GeyserPlatform == "spigot")
            {
                // Geyser for Paper/Purpur comes straight from GeyserMC. It speaks the newest Minecraft
                // version, so an older server also needs ViaVersion to understand it.
                specs.Add(new PluginSpec { Geyser = "geyser" });
                specs.Add(new PluginSpec { Geyser = "floodgate" });
                try
                {
                    var newest = (await Mojang.ReleasesAsync())[0];
                    if (VersionOrder.Compare(State.InstalledMcVersion, newest) < 0 && !Listed("viaversion"))
                        specs.Add(new PluginSpec { Modrinth = "viaversion", IsDependency = true });
                }
                catch (Exception ex) { Log.WriteFileOnly("WARN couldn't check newest Minecraft for ViaVersion: " + ex.Message); }
            }
            else if (EffectiveCrossplay)
            {
                // Mod versions of Geyser only support one Minecraft version each, so Modrinth picks the matching build.
                if (!Listed("geyser")) specs.Add(new PluginSpec { Modrinth = "geyser" });
                if (!Listed("floodgate")) specs.Add(new PluginSpec { Modrinth = "floodgate" });
            }
            return specs;
        }

        public string DescribeInstalled()
        {
            var sw = InstalledSoftware;
            if (sw == null || State.InstalledMcVersion == null) return "nothing yet";
            string loader = State.InstalledLoader;
            switch (sw.Id)
            {
                case "paper":
                case "purpur": loader = "build #" + loader; break;
                case "fabric": loader = "loader " + loader; break;
                case "vanilla": loader = null; break;
            }
            return sw.Name + " " + State.InstalledMcVersion + (loader != null ? " (" + loader + ")" : "");
        }

        private bool LaunchFilesPresent()
        {
            if (State.LaunchArgs == null || State.LaunchArgs.Count == 0) return false;
            foreach (var a in State.LaunchArgs)
            {
                var file = a.StartsWith("@") ? a.Substring(1) : a.EndsWith(".jar") ? a : null;
                if (file != null && !File.Exists(Path.Combine(AppPaths.Server, file))) return false;
            }
            return true;
        }

        /// <summary>Removes the old server program before a different one is installed (worlds and add-ons stay).</summary>
        private static void CleanSoftwareFiles()
        {
            foreach (var f in new[] { "server.jar", "run.bat", "run.sh", "user_jvm_args.txt" })
            {
                try { var p = Path.Combine(AppPaths.Server, f); if (File.Exists(p)) File.Delete(p); } catch { }
            }
            foreach (var d in new[] { "libraries", "versions", ".fabric", "cache" })
                FileUtil.DeleteDirectory(Path.Combine(AppPaths.Server, d));
        }

        // ------------------------------------------------------------------ world version guard

        /// <summary>Minecraft can't open a world in an older version than it was last played on.</summary>
        public void GuardWorldVersion(string softwareId, string targetVersion)
        {
            var worldVersion = WorldVersionFor(softwareId);
            if (string.IsNullOrEmpty(worldVersion)) return;
            if (VersionOrder.Compare(targetVersion, worldVersion) < 0)
                throw new FriendlyException(
                    "This world was last played on Minecraft " + worldVersion + ", and Minecraft can't move a world back to " + targetVersion + ".",
                    "Pick " + worldVersion + " or newer, or start a new world (Server Settings > World > Start a new world).");
        }

        private static string FamilyOf(string softwareId)
        {
            try { return softwareId == null ? null : ServerSoftware.Get(softwareId).WorldFamily; }
            catch (FriendlyException) { return "bukkit"; }
        }

        /// <summary>True if switching to this software uses a different world than the current one.</summary>
        public bool ChangesWorldFamily(string softwareId) =>
            State.InstalledSoftware != null && FamilyOf(State.InstalledSoftware) != FamilyOf(softwareId);

        private static string StashFolder(string family) => Path.Combine(AppPaths.Server, "worlds-" + family);

        /// <summary>Version of the world that software would use (the other family's stored world when switching families).</summary>
        public string WorldVersionFor(string softwareId)
        {
            if (!ChangesWorldFamily(softwareId))
                return Backups.WorldFolders().Count > 0 ? State.WorldVersion : null;
            var file = Path.Combine(StashFolder(FamilyOf(softwareId)), "world-version.txt");
            var v = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
            return v.Length > 0 ? v : null;
        }

        /// <summary>Puts the current world aside for its family and brings back the other family's world, if it has one.</summary>
        private void SwapWorlds(string fromFamily, string toFamily)
        {
            var worlds = Backups.WorldFolders();
            if (worlds.Count > 0)
            {
                var stash = StashFolder(fromFamily);
                if (Directory.Exists(stash))
                    Directory.Move(stash, Path.Combine(AppPaths.Backups, "old-" + Path.GetFileName(stash) + "-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")));
                Directory.CreateDirectory(stash);
                foreach (var w in worlds) Directory.Move(w, Path.Combine(stash, Path.GetFileName(w)));
                File.WriteAllText(Path.Combine(stash, "world-version.txt"), State.WorldVersion ?? "");
                Log.Info("Your " + ServerSoftware.FamilyName(fromFamily) + " world was put aside. It comes back if you switch back.");
            }

            State.WorldVersion = null;
            var back = StashFolder(toFamily);
            if (Directory.Exists(back))
            {
                foreach (var d in Directory.GetDirectories(back)) Directory.Move(d, Path.Combine(AppPaths.Server, Path.GetFileName(d)));
                var file = Path.Combine(back, "world-version.txt");
                var v = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
                State.WorldVersion = v.Length > 0 ? v : null;
                FileUtil.DeleteDirectory(back);
                Log.Info("Your " + ServerSoftware.FamilyName(toFamily) + " world is back.");
            }
            else Log.Info("A new world will be made for " + ServerSoftware.FamilyName(toFamily) + ".");
            State.Save();
        }

        /// <summary>Runs once the server reports "Done": records the world's version and finishes crossplay setup.</summary>
        private void OnServerRunning()
        {
            RememberWorldVersion();
            if (CrossplayActive && GeyserConfig.Apply(Software))
                Server.SendCommand("geyser reload");
            if (CrossplayActive)
                Log.Success("Bedrock players on the same Wi-Fi can join at " + LocalIp + " port " + GeyserConfig.BedrockPort + ".");
        }

        private static readonly System.Text.RegularExpressions.Regex SignalingAddress =
            new System.Text.RegularExpressions.Regex(@"Players can join at https?://([A-Za-z0-9.-]+)", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Geyser prints the address GeyserMC's signaling service gave this server; show it on the Dashboard.</summary>
        private void WatchGeyserOutput(string line)
        {
            if (line.IndexOf("Players can join at", StringComparison.Ordinal) < 0) return;
            var m = SignalingAddress.Match(line);
            if (!m.Success) return;
            var host = m.Groups[1].Value;
            _ui.Post(_ =>
            {
                if (Settings.BedrockInternetAddress != host)
                {
                    Settings.BedrockInternetAddress = host;
                    Settings.Save();
                }
                Log.Success("Bedrock players anywhere can join at " + host + " port 443 (Bedrock 26.40 or newer).");
                RaiseChanged();
            }, null);
        }

        private void RememberWorldVersion()
        {
            var v = State.InstalledMcVersion;
            if (string.IsNullOrEmpty(v)) return;
            if (State.WorldVersion == null || VersionOrder.Compare(v, State.WorldVersion) > 0)
            {
                State.WorldVersion = v;
                State.Save();
            }
        }

        // ------------------------------------------------------------------ changing the server type

        /// <summary>Applies a server type chosen in the app. Reinstalls (and restarts if running).</summary>
        public async Task ChangeServerTypeAsync(string software, string version, bool crossplay)
        {
            var sw = ServerSoftware.Get(software);
            if (crossplay && !sw.SupportsCrossplay)
                throw new FriendlyException("Crossplay needs Paper, Purpur, Fabric or NeoForge.", "Pick one of those, or turn crossplay off.");

            SetBusy("Checking " + sw.Name + " versions...");
            try
            {
                var concrete = await sw.ResolveVersionAsync(version);
                GuardWorldVersion(sw.Id, concrete);
            }
            finally { ClearBusy(); }

            Settings.SoftwareOverride = sw.Id == Manifest.Software ? null : sw.Id;
            Settings.VersionOverride = string.Equals(version, Manifest.MinecraftVersion, StringComparison.OrdinalIgnoreCase) ? null : version;
            Settings.CrossplayOverride = crossplay == Manifest.Crossplay ? (bool?)null : crossplay;
            Settings.Save();
            Log.Info("Server type set to " + sw.Name + " " + version + (crossplay ? " with Bedrock crossplay" : "") + ".");

            if (Server.IsAlive) await RestartAsync(backupFirst: Settings.BackupBeforeUpdates);
            else
            {
                await _op.WaitAsync();
                try { await PrepareAsync(); }
                finally { ClearBusy(); _op.Release(); }
            }
            RaiseChanged();
        }

        /// <summary>Moves the current world into the backups folder so the next start makes a fresh one.</summary>
        public void StartNewWorld()
        {
            if (Server.IsAlive) throw new FriendlyException("Stop the server first.");
            var worlds = Backups.WorldFolders();
            if (worlds.Count == 0) { Log.Info("There's no world yet - one is created on the next start."); return; }
            var aside = Path.Combine(AppPaths.Backups, "old-world-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(aside);
            foreach (var w in worlds) Directory.Move(w, Path.Combine(aside, Path.GetFileName(w)));
            State.WorldVersion = null;
            State.Save();
            Log.Success("The old world was moved to backups\\" + Path.GetFileName(aside) + ". A new world is created on the next start.");
            RaiseChanged();
        }
    }
}
