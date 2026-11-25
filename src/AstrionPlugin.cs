using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AstrionPlaynite
{
    /// <summary>
    /// Lets the Astrion Custom Dashboard remote see and drive Playnite over the local network: the games (with their
    /// covers), the game running, starting a game, and putting the PC to sleep or shutting it down.
    /// A small HTTP server on <see cref="Port"/>; every call but /info and the covers needs the code
    /// shown in Playnite (Extensions › Astrion Custom Dashboard), typed once on the remote's web page.
    /// </summary>
    public class AstrionPlugin : GenericPlugin
    {
        public const int Port = 8095;
        public const string Version = "0.1.3";

        private static readonly ILogger log = LogManager.GetLogger();

        private SynchronizationContext ui;
        private HttpBridge server;
        private string code;
        private volatile Game running;

        /// <summary>Windows' session is locked (the sign-in screen shows): the remote then unlocks it before starting a game.</summary>
        private volatile bool locked;

        public override Guid Id { get; } = Guid.Parse("6f1c9a52-3d47-4e0b-9a1e-5b7f2c8d4a31");

        public AstrionPlugin(IPlayniteAPI api) : base(api)
        {
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            ui = SynchronizationContext.Current;
            code = LoadCode();
            try
            {
                server = new HttpBridge(Port, Handle, log);
                server.Start();
                log.Info("Astrion Custom Dashboard: listening on port " + Port + ", code " + code);
            }
            catch (Exception e)
            {
                log.Error(e, "Astrion Custom Dashboard: can't listen on port " + Port);
            }
            ShowCode();
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            if (server != null) server.Stop();
        }

        /**
         * Windows shows its sign-in or lock screen. Its LogonUI process runs exactly then; checked
         * when asked, it's right at once after a wake, when the session-switch event may not have
         * reached Playnite yet (0.1.3).
         */
        private bool IsLocked()
        {
            try
            {
                if (Process.GetProcessesByName("LogonUI").Length > 0) return true;
            }
            catch (Exception)
            {
            }
            return locked;
        }

        /// <summary>"desktop" or "fullscreen": the Playnite window the remote finds (0.1.3).</summary>
        private string ModeName()
        {
            try
            {
                return PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen ? "fullscreen" : "desktop";
            }
            catch (Exception)
            {
                return "desktop";
            }
        }

        /**
         * Switches Playnite to fullscreen or desktop (0.1.3), the way its own command line does it:
         * launched with --startfullscreen / --startdesktop while it runs, Playnite tells the running
         * one to switch. Playnite restarts in the other mode, and so does this extension: the
         * remote waits for it to answer again.
         */
        private HttpResponse SwitchMode(string mode)
        {
            if (mode != "fullscreen" && mode != "desktop") return HttpResponse.Error(404, "unknown mode");
            if (ModeName() == mode) return HttpResponse.Json("{\"ok\":true,\"mode\":\"" + mode + "\"}");
            var exe = Path.Combine(PlayniteApi.Paths.ApplicationPath ?? "", "Playnite.DesktopApp.exe");
            if (!File.Exists(exe)) return HttpResponse.Error(500, "Playnite.DesktopApp.exe not found");
            log.Info("Astrion Custom Dashboard: switching to " + mode);
            Process.Start(new ProcessStartInfo(exe, mode == "fullscreen" ? "--startfullscreen" : "--startdesktop") { UseShellExecute = false });
            return HttpResponse.Json("{\"ok\":true,\"mode\":\"" + mode + "\",\"switching\":true}");
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock) locked = true;
            else if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.SessionLogon) locked = false;
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            running = args.Game;
            log.Info("Astrion Custom Dashboard: game started: " + (args.Game == null ? "?" : args.Game.Name));
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            var r = running;
            if (r != null && args.Game != null && r.Id == args.Game.Id) running = null;
            log.Info("Astrion Custom Dashboard: game stopped: " + (args.Game == null ? "?" : args.Game.Name));
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                Description = "Show the code for the remote",
                MenuSection = "@Astrion Custom Dashboard",
                Action = a => ShowCode()
            };
        }

        private void ShowCode()
        {
            PlayniteApi.Notifications.Add(
                "astrion-code",
                "Astrion Custom Dashboard: code " + code + " (this PC: port " + Port + "). Type it on the remote's web page.",
                NotificationType.Info);
        }

        /// <summary>A random 6-digit code, kept in the plugin's own folder.</summary>
        private string LoadCode()
        {
            var path = Path.Combine(GetPluginUserDataPath(), "code.txt");
            try
            {
                if (File.Exists(path))
                {
                    var saved = File.ReadAllText(path).Trim();
                    if (saved.Length == 6 && saved.All(char.IsDigit)) return saved;
                }
            }
            catch (Exception e)
            {
                log.Error(e, "Astrion Custom Dashboard: can't read the code");
            }
            var bytes = new byte[4];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var fresh = (BitConverter.ToUInt32(bytes, 0) % 1000000).ToString("D6");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, fresh);
            }
            catch (Exception e)
            {
                log.Error(e, "Astrion Custom Dashboard: can't save the code");
            }
            return fresh;
        }

        // ---- HTTP ----

        private HttpResponse Handle(HttpRequest req)
        {
            var path = req.Path;
            if (req.Method == "GET" && path == "/info") return HttpResponse.Json(InfoJson());
            if (req.Method == "GET" && path.StartsWith("/cover/")) return Picture(path.Substring(7), g => g.CoverImage);
            if (req.Method == "GET" && path.StartsWith("/background/")) return Picture(path.Substring(12), g => g.BackgroundImage);
            if (!Authorized(req)) return HttpResponse.Error(401, "wrong or missing code (X-Astrion-Code)");
            if (req.Method == "GET" && path == "/state") return HttpResponse.Json(StateJson());
            if (req.Method == "GET" && path == "/games") return HttpResponse.Json(OnUi(GamesJson));
            if (req.Method == "GET" && path.StartsWith("/game/")) return Details(path.Substring(6));
            if (req.Method == "POST" && path.StartsWith("/start/")) return Start(path.Substring(7));
            if (req.Method == "POST" && path.StartsWith("/power/")) return Power(path.Substring(7));
            if (req.Method == "POST" && path.StartsWith("/mode/")) return SwitchMode(path.Substring(6));
            return HttpResponse.Error(404, "not found");
        }

        private bool Authorized(HttpRequest req)
        {
            string given;
            if (!req.Headers.TryGetValue("x-astrion-code", out given) || given == null || code == null) return false;
            given = given.Trim();
            if (given.Length != code.Length) return false;
            var diff = 0;
            for (var i = 0; i < code.Length; i++) diff |= given[i] ^ code[i];
            return diff == 0;
        }

        private T OnUi<T>(Func<T> f)
        {
            if (ui == null) return f();
            var result = default(T);
            Exception error = null;
            ui.Send(_ =>
            {
                try
                {
                    result = f();
                }
                catch (Exception e)
                {
                    error = e;
                }
            }, null);
            if (error != null) throw error;
            return result;
        }

        private Game Find(string id)
        {
            Guid guid;
            if (!Guid.TryParse(id, out guid)) return null;
            return OnUi(() => PlayniteApi.Database.Games.FirstOrDefault(g => g.Id == guid));
        }

        private Game Running()
        {
            var r = running;
            if (r != null) return r;
            return OnUi(() => PlayniteApi.Database.Games.FirstOrDefault(g => g.IsRunning));
        }

        private string InfoJson()
        {
            var macs = new List<string>();
            try
            {
                macs = WakeMacs();
            }
            catch (Exception e)
            {
                log.Error(e, "Astrion Custom Dashboard: can't list the network cards");
            }
            var j = new JsonWriter();
            j.Begin();
            j.Prop("app", "playnite");
            j.Prop("plugin", Version);
            j.Prop("name", Environment.MachineName);
            j.Prop("port", Port);
            j.Prop("mode", ModeName());
            j.Key("macs");
            j.BeginArray();
            foreach (var m in macs) j.Value(m);
            j.EndArray();
            j.End();
            return j.ToString();
        }

        private static readonly string[] VirtualWords = { "virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "vpn", "tap-", "tun", "wireguard", "tailscale", "zerotier", "hamachi", "bluetooth", "loopback", "npcap", "docker", "wsl" };

        /**
         * The MAC addresses that can wake this PC, best first: a physical wired card with a gateway
         * (the one Wake-on-LAN reaches when the PC is off), then Wi-Fi; virtual cards (Hyper-V, VPN,
         * VirtualBox…) last, as they never wake anything.
         */
        private static List<string> WakeMacs()
        {
            var ranked = new List<KeyValuePair<int, string>>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                var b = nic.GetPhysicalAddress().GetAddressBytes();
                if (b.Length != 6) continue;
                var text = ((nic.Name ?? "") + " " + (nic.Description ?? "")).ToLowerInvariant();
                var isVirtual = VirtualWords.Any(w => text.Contains(w));
                var hasGateway = false;
                try
                {
                    hasGateway = nic.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.ToString().StartsWith("0."));
                }
                catch (Exception)
                {
                }
                var rank = (isVirtual ? 100 : 0) + (hasGateway ? 0 : 10) + (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1);
                ranked.Add(new KeyValuePair<int, string>(rank, string.Join(":", b.Select(x => x.ToString("x2")))));
                log.Info("Astrion Custom Dashboard: network card " + nic.Name + " (" + nic.Description + "), rank " + rank);
            }
            return ranked.OrderBy(r => r.Key).Select(r => r.Value).Distinct().ToList();
        }

        private string StateJson()
        {
            var g = Running();
            var j = new JsonWriter();
            j.Begin();
            j.Key("running");
            if (g == null) j.Null();
            else WriteGame(j, g);
            j.Prop("locked", IsLocked());
            j.Prop("mode", ModeName());
            j.End();
            return j.ToString();
        }

        private string GamesJson()
        {
            var games = PlayniteApi.Database.Games
                .Where(g => g.IsInstalled && !g.Hidden)
                .OrderByDescending(g => g.LastActivity ?? DateTime.MinValue)
                .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var j = new JsonWriter();
            j.Begin();
            j.Key("games");
            j.BeginArray();
            foreach (var g in games) WriteGame(j, g);
            j.EndArray();
            j.End();
            return j.ToString();
        }

        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static void WriteGame(JsonWriter j, Game g)
        {
            j.Begin();
            j.Prop("id", g.Id.ToString());
            j.Prop("name", g.Name ?? "");
            j.Prop("cover", !string.IsNullOrEmpty(g.CoverImage));
            j.Prop("playtime", (long)g.Playtime);
            j.Prop("lastPlayed", g.LastActivity.HasValue ? (long)(g.LastActivity.Value.ToUniversalTime() - Epoch).TotalSeconds : 0L);
            j.End();
        }

        /// <summary>A game's cover or background image, as Playnite keeps it (a file, or a web address).</summary>
        private HttpResponse Picture(string id, Func<Game, string> which)
        {
            var g = Find(id);
            var image = g == null ? null : which(g);
            if (string.IsNullOrEmpty(image)) return HttpResponse.Error(404, "no picture");
            if (image.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return HttpResponse.Redirect(image);
            var file = OnUi(() => PlayniteApi.Database.GetFullFilePath(image));
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return HttpResponse.Error(404, "no picture");
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var type = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : ext == ".gif" ? "image/gif" : "image/jpeg";
            return new HttpResponse(200, type, File.ReadAllBytes(file)) { CacheSeconds = 86400 };
        }

        /// <summary>A game's sheet for the remote: its description as plain text, genres, studios, year, scores, play time.</summary>
        private HttpResponse Details(string id)
        {
            var g = Find(id);
            if (g == null) return HttpResponse.Error(404, "no such game");
            return HttpResponse.Json(OnUi(() =>
            {
                var j = new JsonWriter();
                j.Begin();
                j.Prop("id", g.Id.ToString());
                j.Prop("name", g.Name ?? "");
                j.Prop("description", PlainText(g.Description));
                Names(j, "genres", g.Genres);
                Names(j, "developers", g.Developers);
                Names(j, "publishers", g.Publishers);
                Names(j, "platforms", g.Platforms);
                j.Prop("year", (long)(g.ReleaseYear ?? 0));
                j.Prop("critic", (long)(g.CriticScore ?? 0));
                j.Prop("community", (long)(g.CommunityScore ?? 0));
                j.Prop("playtime", (long)g.Playtime);
                j.Prop("lastPlayed", g.LastActivity.HasValue ? (long)(g.LastActivity.Value.ToUniversalTime() - Epoch).TotalSeconds : 0L);
                j.Prop("cover", !string.IsNullOrEmpty(g.CoverImage));
                j.Prop("background", !string.IsNullOrEmpty(g.BackgroundImage));
                j.End();
                return j.ToString();
            }));
        }

        private static void Names<T>(JsonWriter j, string key, IEnumerable<T> items) where T : DatabaseObject
        {
            j.Key(key);
            j.BeginArray();
            if (items != null)
            {
                foreach (var name in items.Where(i => i != null && !string.IsNullOrWhiteSpace(i.Name)).Select(i => i.Name).Distinct()) j.Value(name);
            }
            j.EndArray();
        }

        private const int MaxDescription = 4000;

        /// <summary>A description written in HTML (as metadata sources give it) as plain text: paragraphs kept, tags dropped.</summary>
        private static string PlainText(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            var text = Regex.Replace(html, @"<\s*(br|/p|/div|/li|/h[1-6])\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<\s*li[^>]*>", "• ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<[^>]+>", "");
            text = WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"[ \t\r\f\v]+", " ");
            text = Regex.Replace(text, @" *\n *", "\n");
            text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
            return text.Length > MaxDescription ? text.Substring(0, MaxDescription).TrimEnd() + "…" : text;
        }

        private HttpResponse Start(string id)
        {
            var g = Find(id);
            if (g == null) return HttpResponse.Error(404, "no such game");
            log.Info("Astrion Custom Dashboard: the remote starts " + g.Name);
            var guid = g.Id;
            if (ui != null) ui.Post(_ => PlayniteApi.StartGame(guid), null);
            else PlayniteApi.StartGame(guid);
            return HttpResponse.Json("{\"ok\":true}");
        }

        private HttpResponse Power(string action)
        {
            if (action != "sleep" && action != "shutdown" && action != "restart") return HttpResponse.Error(400, "sleep, shutdown or restart");
            log.Info("Astrion Custom Dashboard: the remote asks to " + action);
            // Answered first: the PC goes a moment later.
            new Thread(() =>
            {
                Thread.Sleep(1000);
                try
                {
                    if (action == "sleep") SetSuspendState(false, true, false);
                    else Process.Start(new ProcessStartInfo("shutdown", action == "shutdown" ? "/s /t 0" : "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
                }
                catch (Exception e)
                {
                    log.Error(e, "Astrion Custom Dashboard: " + action + " failed");
                }
            }) { IsBackground = true }.Start();
            return HttpResponse.Json("{\"ok\":true}");
        }

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    }
}
