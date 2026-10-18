using System;
using System.Threading;
using System.Threading.Tasks;

namespace CardboardBox.Core
{
    /// <summary>The playit.gg half of the controller: a tunnel that runs alongside the server.</summary>
    public partial class AppController
    {
        public Playit Playit { get; } = new Playit();

        public bool UsingPlayit => Settings.PlayitEnabled && Settings.HasPlayit;

        private string AgentVersion => string.IsNullOrEmpty(State.PlayitVersion) ? "1.0.10" : State.PlayitVersion.TrimStart('v', 'V');

        /// <summary>The address friends outside the home should use (playit.gg if set up, else the public IP).</summary>
        public string InternetAddress =>
            UsingPlayit && !string.IsNullOrEmpty(Settings.PlayitAddress) ? Settings.PlayitAddress
            : PublicIp == null ? null : PublicIp + (Port == 25565 ? "" : ":" + Port);

        /// <summary>Links this PC to a playit.gg account (the person approves it in the browser) and creates the tunnel.</summary>
        public async Task<PlayitTunnel> SetupPlayitAsync(string claimCode, Action<string> status, CancellationToken token)
        {
            await Playit.EnsureAgentAsync(State, null);
            var secret = await Playit.ClaimAsync(claimCode, AgentVersion, status, token);
            Settings.PlayitSecret = secret;
            Settings.PlayitEnabled = true;
            Settings.Save();
            Log.Success("CardboardBox is now linked to your playit.gg account.");
            await Playit.NameAgentAsync(secret);

            // The agent has to connect once before playit lets it own a tunnel.
            status("Connecting to playit.gg...");
            Playit.Stop();
            Playit.Start(secret);
            try
            {
                status("Creating your Minecraft tunnel (this can take a minute)...");
                var tunnel = await Playit.EnsureTunnelAsync(secret, Port);
                if (tunnel != null) SavePlayitAddress(tunnel);
                return tunnel;
            }
            finally
            {
                if (!Server.IsAlive) Playit.Stop(); // it starts again with the server
                RaiseChanged();
            }
        }

        /// <summary>Turns the tunnel on or off without forgetting the playit.gg link.</summary>
        public void SetPlayitEnabled(bool on)
        {
            Settings.PlayitEnabled = on;
            Settings.Save();
            if (!on) Playit.Stop();
            else if (Server.IsAlive) _ = StartPlayitAsync();
            RaiseChanged();
        }

        /// <summary>Removes the playit.gg key from this PC.</summary>
        public void ForgetPlayit()
        {
            Playit.Stop();
            Settings.PlayitSecret = null;
            Settings.PlayitEnabled = false;
            Settings.PlayitAddress = null;
            Settings.PlayitAddressWithPort = null;
            Settings.Save();
            Log.Info("Disconnected from playit.gg. (You can also remove the agent at playit.gg.)");
            RaiseChanged();
        }

        /// <summary>Runs right after the server starts. Problems never stop the server itself.</summary>
        private async Task StartPlayitAsync()
        {
            if (!UsingPlayit) return;
            try
            {
                await Playit.EnsureAgentAsync(State, Progress);
                ClearBusy();
                var secret = Settings.PlayitSecret;
                if (!Server.IsAlive || !UsingPlayit) return;
                Playit.Start(secret);
                var tunnel = await Playit.EnsureTunnelAsync(secret, Port);

                // A brand-new tunnel can take a little while to get its address.
                for (int i = 0; tunnel == null && i < 12 && Server.IsAlive; i++)
                {
                    await Task.Delay(10000);
                    tunnel = await Playit.EnsureTunnelAsync(secret, Port);
                }
                if (tunnel == null)
                {
                    Log.Warn("playit.gg hasn't given the tunnel an address yet. Check your tunnels at playit.gg.");
                    return;
                }
                SavePlayitAddress(tunnel);
                if (tunnel.Disabled) Log.Warn("The playit.gg tunnel is switched off on playit.gg - turn it back on in your playit.gg dashboard.");
                else Log.Success("Friends anywhere can join at " + tunnel.Address + " (through playit.gg).");
            }
            catch (Exception ex)
            {
                ClearBusy();
                Log.Error("playit.gg didn't start (friends on your home network can still join)", ex);
            }
        }

        private void SavePlayitAddress(PlayitTunnel tunnel)
        {
            if (Settings.PlayitAddress == tunnel.Address && Settings.PlayitAddressWithPort == tunnel.AddressWithPort) return;
            Settings.PlayitAddress = tunnel.Address;
            Settings.PlayitAddressWithPort = tunnel.AddressWithPort;
            Settings.Save();
            RaiseChanged();
        }
    }
}
