using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CardboardBox.Core
{
    /// <summary>IP addresses, port checks and automatic router port-forwarding (UPnP).</summary>
    public static class NetInfo
    {
        /// <summary>This PC's address on the home network (what friends in the same house connect to).</summary>
        public static string LocalIPv4()
        {
            try
            {
                var candidates = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .Select(n => n.GetIPProperties())
                    .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                    .SelectMany(p => p.UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .ToList();
                return candidates.FirstOrDefault() ?? "127.0.0.1";
            }
            catch { return "127.0.0.1"; }
        }

        /// <summary>The internet address of the home network (what friends elsewhere connect to).</summary>
        public static async Task<string> PublicIPv4Async()
        {
            foreach (var url in new[] { "https://api.ipify.org", "https://checkip.amazonaws.com" })
            {
                try
                {
                    var r = await Net.SendAsync(System.Net.Http.HttpMethod.Get, url, timeoutSeconds: 10, attempts: 1).ConfigureAwait(false);
                    var ip = r.Body?.Trim();
                    if (r.Ok && IPAddress.TryParse(ip, out _)) return ip;
                }
                catch { }
            }
            return null;
        }

        public static bool IsPortFree(int port)
        {
            try
            {
                var l = new TcpListener(IPAddress.Any, port);
                l.Start();
                l.Stop();
                return true;
            }
            catch (SocketException) { return false; }
        }

        /// <summary>
        /// Asks the router to forward the Minecraft port to this PC using UPnP (built into Windows).
        /// Many routers support it; some have it switched off. Returns a message to show.
        /// </summary>
        public static Task<string> TryPortForwardAsync(int port)
        {
            var tcs = new TaskCompletionSource<string>();
            var t = new Thread(() =>
            {
                try
                {
                    var type = Type.GetTypeFromProgID("HNetCfg.NATUPnP");
                    if (type == null) { tcs.SetResult("This version of Windows doesn't include the UPnP helper."); return; }
                    dynamic nat = Activator.CreateInstance(type);
                    dynamic maps = nat.StaticPortMappingCollection;
                    if (maps == null)
                    {
                        tcs.SetResult("Your router didn't answer. Automatic port forwarding (UPnP) is probably switched off in the router's settings. " +
                                      "You can forward port " + port + " (TCP) to " + LocalIPv4() + " by hand in the router, or use a free tunnel such as playit.gg.");
                        return;
                    }
                    try { maps.Remove(port, "TCP"); } catch { }
                    maps.Add(port, "TCP", port, LocalIPv4(), true, "CardboardBox Minecraft");
                    tcs.SetResult("OK");
                }
                catch (Exception ex)
                {
                    tcs.SetResult("The router refused automatic port forwarding (" + ex.Message.Trim() + "). " +
                                  "Forward port " + port + " (TCP) to " + LocalIPv4() + " in the router settings, or use playit.gg.");
                }
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return tcs.Task;
        }
    }
}
