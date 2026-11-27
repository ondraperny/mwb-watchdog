// MWB Keepalive - keeps PowerToys "Mouse Without Borders" (MWB) connected.
//
// MWB only retries a lost connection when Windows reports that the network went from
// "unavailable" to "available". On PCs with VPN / virtual adapters that never happens, so
// after a Wi-Fi blip, router restart or sleep, MWB can stay disconnected until the Reconnect
// hotkey is pressed. This program watches MWB and presses that "button" for you - but only
// when the other PC is actually reachable.
//
// Built by install.ps1 with the C# compiler that ships with Windows (.NET Framework 4.x),
// so it is plain C# 5. Usage:
//   MWBKeepalive.exe              run in the tray (what the scheduled task does)
//   MWBKeepalive.exe --start      Start menu entry: start it, or say it is already running
//   MWBKeepalive.exe --status     print the current state
//   MWBKeepalive.exe --reconnect  tell MWB to reconnect now

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("MWB Keepalive")]
[assembly: AssemblyProduct("MWB Keepalive")]
[assembly: AssemblyDescription("Keeps PowerToys Mouse Without Borders connected")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace MwbKeepalive
{
    static class Config
    {
        public const string MwbProcessName     = "PowerToys.MouseWithoutBorders";
        public const string PipeName           = @"MouseWithoutBorders\SettingsSync";
        // Same event the MWB hotkey / Command Palette "Reconnect" uses (fallback only).
        public const string ReconnectEventName = @"Local\PowerToysMWB-ReconnectEvent-b8d7c6a5-f4e3-2b1c-0a9d-8e7f6a5b4c3d";
        public const string MutexName          = @"Local\MWBKeepalive-SingleInstance";
        public const string TaskName           = "MWB Keepalive"; // scheduled task created by install.ps1

        public const int LoopMs             = 2000; // how often MWB's state is checked
        public const int ProbeEverySec      = 3;    // how often a disconnected PC is knocked on
        public const int ProbeTimeoutMs     = 1000; // knock timeout
        public const int DisconnectGraceSec = 5;    // let MWB fix quick drops itself first
        public const int QuietSec           = 6;    // act only after MWB showed no reconnect activity this long
        public const int InProgressMaxSec   = 40;   // "Resolving/Connecting/Handshaking" longer than this = stuck
        public const int PipeBusyMaxSec     = 30;   // MWB's pipe is busy while it reconnects
        public const int StartupGraceSec    = 20;   // MWB reconnects by itself right after it starts
        public const int ResumeGapSec       = 20;   // loop pause longer than this = PC was asleep
        public const int ResumeSettleSec    = 5;    // wait for Wi-Fi after wake before acting
        public const int DnsRefreshSec      = 600;  // only for PCs without an IP mapping
        public const int ProcessCheckSec    = 15;   // listing processes is the costliest check, so do it rarely
        public const int ConnectedCode      = 9;
        public const long MaxLogBytes       = 512 * 1024;
        public static readonly int[] BackoffSec = { 15, 30, 60, 120, 300 };

        static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static readonly string AppDir       = Path.Combine(LocalAppData, "MWBKeepalive");
        public static readonly string LogFile      = Path.Combine(AppDir, "keepalive.log");
        public static readonly string SettingsFile = Path.Combine(LocalAppData, @"Microsoft\PowerToys\MouseWithoutBorders\settings.json");

        static readonly string[] StatusNames = { "NA", "Resolving", "Connecting", "Handshaking", "Error", "ForceClosed", "InvalidKey", "Timeout", "SendError", "Connected" };
        public static string StatusName(int code)
        {
            return code >= 0 && code < StatusNames.Length ? StatusNames[code] : "code " + code;
        }
    }

    // ------------------------------------------------------------------ logging
    static class Log
    {
        static readonly object Gate = new object();

        public static void Write(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message;
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Config.AppDir);
                    var info = new FileInfo(Config.LogFile);
                    if (info.Exists && info.Length > Config.MaxLogBytes)
                    {
                        string old = Config.LogFile + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(Config.LogFile, old);
                    }
                    File.AppendAllText(Config.LogFile, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch { }
            }
        }
    }

    // ------------------------------------------------------------ MWB settings
    sealed class MwbSettings
    {
        public int MessagePort = 15101; // MWB's main port = TCPPort + 1 (TCPPort itself is for the clipboard)
        public readonly List<string> Peers = new List<string>();
        public readonly Dictionary<string, List<IPAddress>> Map = new Dictionary<string, List<IPAddress>>(StringComparer.OrdinalIgnoreCase);

        static MwbSettings cached;
        static DateTime cachedStamp;

        public static MwbSettings Load()
        {
            try
            {
                if (!File.Exists(Config.SettingsFile)) return null;
                DateTime stamp = File.GetLastWriteTimeUtc(Config.SettingsFile);
                if (cached != null && stamp == cachedStamp) return cached;

                string text;
                using (var fs = new FileStream(Config.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs)) text = reader.ReadToEnd();

                var props = Json.Get(Json.Parse(text), "properties");
                if (props == null) return cached;

                var s = new MwbSettings();
                object port = Json.Get(Json.Get(props, "TCPPort"), "value");
                if (port != null) s.MessagePort = Convert.ToInt32(port) + 1;

                var matrix = Json.Get(props, "MachineMatrixString") as object[];
                if (matrix != null)
                {
                    foreach (object o in matrix)
                    {
                        string name = ((o as string) ?? "").Trim();
                        if (name.Length > 0 && !name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                            && !s.Peers.Contains(name, StringComparer.OrdinalIgnoreCase))
                            s.Peers.Add(name);
                    }
                }

                // "IP address mapping": one "<name> <ip>" per line - same parsing as MWB.
                string map = (Json.Get(Json.Get(props, "Name2IP"), "value") as string) ?? "";
                foreach (string line in map.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    IPAddress ip;
                    if (parts.Length >= 2 && IPAddress.TryParse(parts[1].Trim(), out ip))
                    {
                        List<IPAddress> list;
                        if (!s.Map.TryGetValue(parts[0].Trim(), out list)) { list = new List<IPAddress>(); s.Map[parts[0].Trim()] = list; }
                        if (!list.Contains(ip)) list.Add(ip);
                    }
                }

                cached = s;
                cachedStamp = stamp;
                return s;
            }
            catch
            {
                return cached; // file may be mid-write by MWB; keep the previous config
            }
        }
    }

    static class Json
    {
        // Only used from one thread per process (the monitoring thread, or the CLI).
        public static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static object Parse(string text) { return Serializer.DeserializeObject(text); }

        public static object Get(object dict, string key)
        {
            var d = dict as Dictionary<string, object>;
            object value;
            return d != null && d.TryGetValue(key, out value) ? value : null;
        }
    }

    // ---------------------------------------------------------------- MWB pipe
    // JSON-RPC 2.0 over MWB's settings pipe (StreamJsonRpc, "Content-Length" framing).
    // It serves one client at a time - the same channel PowerToys Settings uses.
    static class Mwb
    {
        static int rpcId;

        static object Call(string method, int timeoutMs)
        {
            using (var pipe = new NamedPipeClientStream(".", Config.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                pipe.Connect(1000); // throws if MWB is not running or the pipe is busy
                int id = Interlocked.Increment(ref rpcId);
                byte[] body = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"" + method + "\",\"params\":[]}");
                byte[] head = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
                pipe.Write(head, 0, head.Length);
                pipe.Write(body, 0, body.Length);
                pipe.Flush();

                var received = new MemoryStream();
                var buffer = new byte[16384];
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (true)
                {
                    int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) throw new TimeoutException("MWB did not answer '" + method + "' in time");
                    Task<int> read = pipe.ReadAsync(buffer, 0, buffer.Length);
                    if (!read.Wait(left)) throw new TimeoutException("MWB did not answer '" + method + "' in time");
                    if (read.Result <= 0) throw new IOException("MWB closed the pipe");
                    received.Write(buffer, 0, read.Result);

                    byte[] all = received.ToArray();
                    string headerText = Encoding.ASCII.GetString(all);
                    int sep = headerText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (sep < 0) continue;
                    Match m = Regex.Match(headerText.Substring(0, sep), @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
                    if (!m.Success) throw new IOException("Bad reply from MWB");
                    int length = int.Parse(m.Groups[1].Value);
                    int start = sep + 4;
                    if (all.Length - start < length) continue;

                    var reply = Json.Parse(Encoding.UTF8.GetString(all, start, length));
                    object error = Json.Get(reply, "error");
                    if (error != null) throw new IOException("MWB error: " + (Json.Get(error, "message") ?? "unknown"));
                    return Json.Get(reply, "result");
                }
            }
        }

        // Returns { PC name -> status code } for MWB's outgoing connections.
        public static Dictionary<string, int> GetStates()
        {
            var states = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var list = Call("RequestMachineSocketStateAsync", 3000) as object[];
            if (list != null)
            {
                foreach (object item in list)
                {
                    var name = Json.Get(item, "Name") as string;
                    object status = Json.Get(item, "Status");
                    if (!string.IsNullOrEmpty(name) && status != null) states[name] = Convert.ToInt32(status);
                }
            }
            return states;
        }

        // Same as pressing the Reconnect hotkey.
        public static string Reconnect()
        {
            string pipeError;
            try { Call("Reconnect", 8000); return "pipe"; }
            catch (Exception e) { pipeError = e.Message; }
            try
            {
                EventWaitHandle handle;
                if (EventWaitHandle.TryOpenExisting(Config.ReconnectEventName, out handle))
                {
                    using (handle) handle.Set();
                    return "hotkey event";
                }
            }
            catch { }
            throw new Exception("could not trigger reconnect (" + pipeError + ")");
        }

        public static string ProcessIds()
        {
            Process[] procs = Process.GetProcessesByName(Config.MwbProcessName);
            try { return string.Join(",", procs.Select(p => p.Id).OrderBy(i => i)); }
            finally { foreach (Process p in procs) p.Dispose(); }
        }
    }

    // ------------------------------------------------------------- network bits
    static class Net
    {
        // Established TCP connections on MWB's port, in or out (the OS's view).
        public static int MwbLinkCount(int port)
        {
            int n = 0;
            foreach (TcpConnectionInformation c in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
                if (c.State == TcpState.Established && (c.RemoteEndPoint.Port == port || c.LocalEndPoint.Port == port)) n++;
            return n;
        }

        // A quick TCP "knock" on MWB's port: answers only if the PC is on and MWB runs there.
        public static bool Knock(IEnumerable<IPAddress> addresses, int port)
        {
            foreach (IPAddress a in addresses)
            {
                using (var client = new TcpClient(a.AddressFamily))
                {
                    try
                    {
                        Task connect = client.ConnectAsync(a, port);
                        if (connect.Wait(Config.ProbeTimeoutMs) && client.Connected) return true;
                    }
                    catch { }
                }
            }
            return false;
        }
    }

    // --------------------------------------------------------------- keepalive
    enum Overall { Starting, Connected, Waiting, Reconnecting, Problem }

    sealed class PeerView
    {
        public string Name;
        public string Text;
    }

    sealed class Snapshot
    {
        public Overall State;
        public string Summary;
        public List<PeerView> Peers = new List<PeerView>();
    }

    sealed class PeerState
    {
        public bool? Connected;
        public bool? Reachable;
        public int LastCode;
        public DateTime Since, LastActivity;
        public DateTime LastProbe = DateTime.MinValue, NextTrigger = DateTime.MinValue;
        public DateTime? InProgressSince;
        public int Backoff;

        public PeerState(DateTime now) { Since = now; LastActivity = now; }
    }

    sealed class DnsEntry
    {
        public List<IPAddress> Addresses = new List<IPAddress>();
        public DateTime At = DateTime.MinValue;
        public Task<IPAddress[]> Pending;
    }

    sealed class Keeper
    {
        readonly object gate = new object();
        readonly Dictionary<string, PeerState> peers = new Dictionary<string, PeerState>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, DnsEntry> dns = new Dictionary<string, DnsEntry>(StringComparer.OrdinalIgnoreCase);
        readonly Random random = new Random();
        readonly AutoResetEvent wake = new AutoResetEvent(false);

        Snapshot snapshot = new Snapshot { State = Overall.Starting, Summary = "starting..." };
        volatile bool stopRequested;
        volatile bool manualReconnect;
        string lastProblem = "";
        DateTime? pipeFailSince;
        DateTime reconnectingUntil = DateTime.MinValue;

        DateTime lastTick = DateTime.UtcNow;
        DateTime mwbSeenAt = DateTime.UtcNow;
        DateTime? resumeAt;
        string mwbPids = "";
        bool mwbRunning;
        DateTime lastProcessCheck = DateTime.MinValue;

        public Snapshot GetSnapshot() { lock (gate) return snapshot; }
        void Publish(Snapshot s) { lock (gate) snapshot = s; }

        public void RequestReconnect() { manualReconnect = true; wake.Set(); }
        public void Stop() { stopRequested = true; wake.Set(); }

        public void Run()
        {
            Log.Write("MWB Keepalive started on " + Environment.MachineName + " (pid " + Process.GetCurrentProcess().Id + ").");
            while (!stopRequested)
            {
                try { Tick(); }
                catch (Exception e) { Log.Write("Unexpected error: " + e.Message); Pause(5000); }
                Pause(Config.LoopMs);
            }
        }

        // Sleep that ends early on Exit or "Reconnect now".
        void Pause(int ms)
        {
            if (!stopRequested && !manualReconnect) wake.WaitOne(ms);
        }

        void CheckMwbProcess(DateTime now)
        {
            string pids = Mwb.ProcessIds();
            lastProcessCheck = now;
            mwbRunning = pids.Length > 0;
            if (mwbRunning && pids != mwbPids) { mwbPids = pids; mwbSeenAt = now; } // (re)started
        }

        void Tick()
        {
            DateTime now = DateTime.UtcNow;
            double gap = (now - lastTick).TotalSeconds;
            lastTick = now;
            if (gap > Config.ResumeGapSec)
            {
                Log.Write(string.Format("This PC was asleep or paused for {0:N0}s - will re-check connections after it settles.", gap));
                resumeAt = now.AddSeconds(Config.ResumeSettleSec);
            }

            if (manualReconnect)
            {
                manualReconnect = false;
                TriggerReconnect("(manual, from the tray menu)", null);
                return;
            }

            MwbSettings s = MwbSettings.Load();
            if (s == null) { SetProblem("MWB settings not found - is PowerToys installed?", Overall.Problem); Pause(8000); return; }
            if (s.Peers.Count == 0) { SetProblem("No other PCs in the MWB layout", Overall.Waiting); Pause(8000); return; }

            if (!mwbRunning || (now - lastProcessCheck).TotalSeconds >= Config.ProcessCheckSec) CheckMwbProcess(now);
            if (!mwbRunning) { SetProblem("Mouse Without Borders is not running", Overall.Problem); Pause(3000); return; }

            Dictionary<string, int> states;
            try
            {
                states = Mwb.GetStates();
            }
            catch (Exception e)
            {
                // Not running any more, or just busy (MWB reconnecting / PowerToys Settings open)?
                CheckMwbProcess(now);
                if (!mwbRunning) { SetProblem("Mouse Without Borders is not running", Overall.Problem); return; }
                if (!pipeFailSince.HasValue) pipeFailSince = now;
                foreach (PeerState p in peers.Values) p.LastActivity = now;
                if ((now - pipeFailSince.Value).TotalSeconds > Config.PipeBusyMaxSec)
                    SetProblem("Cannot read MWB state (" + e.Message + ")", Overall.Problem);
                return;
            }
            pipeFailSince = null;
            SetProblem("", Overall.Connected);

            int osLinks = Net.MwbLinkCount(s.MessagePort);
            bool resumeNow = resumeAt.HasValue && now >= resumeAt.Value;
            bool mwbFresh = (now - mwbSeenAt).TotalSeconds < Config.StartupGraceSec;
            var wanted = new List<string>();

            foreach (string peer in s.Peers)
            {
                PeerState ps;
                if (!peers.TryGetValue(peer, out ps)) { ps = new PeerState(now); peers[peer] = ps; }

                int code;
                if (!states.TryGetValue(peer, out code)) code = 0;
                ps.LastCode = code;
                // MWB can report "Connected" for a link the OS has already dropped.
                bool connected = code == Config.ConnectedCode && osLinks > 0;

                if (ps.Connected != connected)
                {
                    if (connected) Log.Write(peer + " connected.");
                    else if (ps.Connected.HasValue) Log.Write(string.Format("{0} disconnected (MWB status: {1}, OS links: {2}).", peer, Config.StatusName(code), osLinks));
                    ps.Connected = connected;
                    ps.Since = now;
                    ps.LastActivity = now;
                    if (connected) { ps.Backoff = 0; ps.NextTrigger = DateTime.MinValue; ps.Reachable = true; }
                }

                // Resolving / Connecting / Handshaking = MWB is working on it right now.
                if (code >= 1 && code <= 3)
                {
                    if (!ps.InProgressSince.HasValue) ps.InProgressSince = now;
                    if ((now - ps.InProgressSince.Value).TotalSeconds < Config.InProgressMaxSec) ps.LastActivity = now;
                }
                else ps.InProgressSince = null;

                if (connected && !resumeNow) continue;

                if (!connected)
                {
                    if ((now - ps.Since).TotalSeconds < Config.DisconnectGraceSec) continue;     // give MWB a moment
                    if ((now - ps.LastActivity).TotalSeconds < Config.QuietSec) continue;        // MWB is still trying
                    if ((now - ps.LastProbe).TotalSeconds < Config.ProbeEverySec && !resumeNow) continue;
                    // Known to be up but a recent reconnect did not stick: stay quiet until the back-off ends.
                    if (ps.Reachable == true && now < ps.NextTrigger && !resumeNow) continue;
                }

                ps.LastProbe = now;
                List<IPAddress> addresses = PeerAddresses(peer, s);
                if (addresses.Count == 0) continue; // name not resolved yet

                bool reachable = Net.Knock(addresses, s.MessagePort);
                bool? was = ps.Reachable;
                ps.Reachable = reachable;

                if (!reachable)
                {
                    if (was != false) Log.Write(peer + " is not reachable (off, asleep or MWB not running there) - waiting for it.");
                    ps.Backoff = 0;
                    ps.NextTrigger = DateTime.MinValue;
                    continue;
                }
                if (was == false) { Log.Write(peer + " is reachable again."); ps.Backoff = 0; ps.NextTrigger = DateTime.MinValue; }

                if (resumeNow || (now >= ps.NextTrigger && !mwbFresh)) wanted.Add(peer);
            }
            if (resumeNow) resumeAt = null;

            PublishPeers(s);

            if (wanted.Count == 0) return;

            // Small random delay so the copies on two PCs rarely fire together,
            // then re-check: the other side may already have fixed it.
            Thread.Sleep(random.Next(200, 1500));
            List<string> still = wanted;
            if (!resumeNow)
            {
                try
                {
                    Dictionary<string, int> again = Mwb.GetStates();
                    int links = Net.MwbLinkCount(s.MessagePort);
                    still = wanted.Where(p => { int c; return !(again.TryGetValue(p, out c) && c == Config.ConnectedCode && links > 0); }).ToList();
                }
                catch { }
            }
            if (still.Count > 0) TriggerReconnect("for: " + string.Join(", ", still), still);
            PublishPeers(s);
        }

        void TriggerReconnect(string why, List<string> forPeers)
        {
            reconnectingUntil = DateTime.UtcNow.AddSeconds(15);
            MwbSettings s = MwbSettings.Load();
            if (s != null) PublishPeers(s);
            try { Log.Write("Reconnect triggered via " + Mwb.Reconnect() + " " + why + "."); }
            catch (Exception e) { Log.Write("Reconnect failed: " + e.Message); }

            if (forPeers != null)
            {
                foreach (string p in forPeers)
                {
                    PeerState ps = peers[p];
                    ps.NextTrigger = DateTime.UtcNow.AddSeconds(Config.BackoffSec[Math.Min(ps.Backoff, Config.BackoffSec.Length - 1)]);
                    ps.Backoff++;
                }
            }
            // MWB is now reconnecting to everyone; give it room before judging.
            foreach (PeerState ps in peers.Values) ps.LastActivity = DateTime.UtcNow;
            Pause(3000);
        }

        List<IPAddress> PeerAddresses(string peer, MwbSettings s)
        {
            List<IPAddress> mapped;
            if (s.Map.TryGetValue(peer, out mapped)) return mapped;

            // No IP mapping: resolve the name in the background (it can be slow) and cache it.
            DnsEntry e;
            if (!dns.TryGetValue(peer, out e)) { e = new DnsEntry(); dns[peer] = e; }
            if (e.Pending != null && e.Pending.IsCompleted)
            {
                if (e.Pending.Status == TaskStatus.RanToCompletion)
                    e.Addresses = e.Pending.Result.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToList();
                else { var ignored = e.Pending.Exception; }
                e.Pending = null;
                e.At = DateTime.UtcNow;
            }
            double age = (DateTime.UtcNow - e.At).TotalSeconds;
            if (e.Pending == null && (age > Config.DnsRefreshSec || (e.Addresses.Count == 0 && age > 30)))
            {
                try { e.Pending = Dns.GetHostAddressesAsync(peer); } catch { e.At = DateTime.UtcNow; }
            }
            return e.Addresses;
        }

        void SetProblem(string text, Overall state)
        {
            if (text != lastProblem)
            {
                if (text.Length > 0) Log.Write(text + " - waiting.");
                else if (lastProblem.Length > 0) Log.Write("MWB is available again.");
                lastProblem = text;
            }
            if (text.Length > 0) Publish(new Snapshot { State = state, Summary = text });
        }

        void PublishPeers(MwbSettings s)
        {
            var snap = new Snapshot();
            int connected = 0, trying = 0, offline = 0;
            bool reconnecting = DateTime.UtcNow < reconnectingUntil;
            foreach (string peer in s.Peers)
            {
                PeerState ps;
                if (!peers.TryGetValue(peer, out ps)) continue;
                string text;
                if (ps.Connected == true) { text = "connected"; connected++; }
                else if (ps.Reachable == false) { text = "off or unreachable"; offline++; }
                else if (reconnecting || (ps.LastCode >= 1 && ps.LastCode <= 3)) { text = "reconnecting..."; trying++; }
                else { text = "disconnected (" + Config.StatusName(ps.LastCode) + ")"; trying++; }
                snap.Peers.Add(new PeerView { Name = peer, Text = text });
            }

            snap.State = trying > 0 ? Overall.Reconnecting : connected > 0 ? Overall.Connected : Overall.Waiting;
            if (snap.Peers.Count == 1)
            {
                snap.Summary = snap.Peers[0].Text == "connected" ? "connected to " + snap.Peers[0].Name : snap.Peers[0].Name + " " + snap.Peers[0].Text;
            }
            else
            {
                var parts = new List<string>();
                if (connected > 0) parts.Add(connected + " connected");
                if (trying > 0) parts.Add(trying + " reconnecting");
                if (offline > 0) parts.Add(offline + " offline");
                snap.Summary = parts.Count > 0 ? string.Join(", ", parts) : "starting...";
            }
            Publish(snap);
        }
    }

    // -------------------------------------------------------------------- tray
    sealed class TrayApp : ApplicationContext
    {
        static readonly Color ConnectedColor = Color.FromArgb(40, 160, 70);

        readonly Keeper keeper = new Keeper();
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer();
        readonly Dictionary<Overall, Icon> icons = new Dictionary<Overall, Icon>();
        readonly Font boldFont;
        Overall shownState = (Overall)(-1);
        string shownText;

        public TrayApp()
        {
            icons[Overall.Starting]     = MakeIcon(Color.FromArgb(140, 140, 140));
            icons[Overall.Waiting]      = MakeIcon(Color.FromArgb(140, 140, 140));
            icons[Overall.Connected]    = MakeIcon(ConnectedColor);
            icons[Overall.Reconnecting] = MakeIcon(Color.FromArgb(235, 140, 20));
            icons[Overall.Problem]      = MakeIcon(Color.FromArgb(215, 50, 50));
            boldFont = new Font(menu.Font, FontStyle.Bold);

            menu.Opening += (s, e) => { BuildMenu(); e.Cancel = false; };
            tray = new NotifyIcon { Icon = icons[Overall.Starting], Text = "MWB Keepalive: starting...", ContextMenuStrip = menu, Visible = true };
            tray.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                // Show the same menu on left click.
                MethodInfo show = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                if (show != null) show.Invoke(tray, null);
            };

            var worker = new Thread(keeper.Run) { IsBackground = true, Name = "keepalive" };
            worker.Start();

            refresh.Interval = 1000;
            refresh.Tick += (s, e) => UpdateTray();
            refresh.Start();
        }

        void UpdateTray()
        {
            Snapshot snap = keeper.GetSnapshot();
            if (snap.State != shownState) { tray.Icon = icons[snap.State]; shownState = snap.State; }
            string text = "MWB: " + snap.Summary;
            if (text.Length > 63) text = text.Substring(0, 60) + "...";
            if (text != shownText) { tray.Text = text; shownText = text; }
        }

        void BuildMenu()
        {
            Snapshot snap = keeper.GetSnapshot();
            menu.Items.Clear();
            menu.Items.Add(new ToolStripMenuItem("MWB Keepalive") { Enabled = false, Font = boldFont });
            if (snap.Peers.Count == 0)
                menu.Items.Add(new ToolStripMenuItem(snap.Summary) { Enabled = false });
            foreach (PeerView p in snap.Peers)
                menu.Items.Add(new ToolStripMenuItem(p.Name + ": " + p.Text) { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Reconnect now", null, (s, e) => keeper.RequestReconnect());
            menu.Items.Add("Open log", null, (s, e) => OpenLog());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit MWB Keepalive (until next sign-in)", null, (s, e) => ExitApp());
        }

        static void OpenLog()
        {
            try { Process.Start(new ProcessStartInfo(Config.LogFile) { UseShellExecute = true }); }
            catch { try { Process.Start("notepad.exe", "\"" + Config.LogFile + "\""); } catch { } }
        }

        void ExitApp()
        {
            Log.Write("MWB Keepalive stopped from the tray menu.");
            refresh.Stop();
            tray.Visible = false;
            keeper.Stop();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { tray.Dispose(); menu.Dispose(); refresh.Dispose(); }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

        static Icon MakeIcon(Color color)
        {
            using (Bitmap bmp = DrawBadge(color, Math.Max(16, SystemInformation.SmallIconSize.Width)))
            {
                IntPtr handle = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        // A round badge in the status colour with a white mouse on it.
        static Bitmap DrawBadge(Color color, int size)
        {
            var bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float pad = size * 0.04f;
                using (var fill = new SolidBrush(color))
                    g.FillEllipse(fill, pad, pad, size - 2 * pad, size - 2 * pad);

                float w = size * 0.40f, h = size * 0.58f;
                float x = (size - w) / 2f, y = (size - h) / 2f;
                using (var body = RoundedRect(x, y, w, h, w / 2f))
                using (var white = new SolidBrush(Color.White))
                    g.FillPath(white, body);
                using (var line = new Pen(color, Math.Max(1f, size / 16f)))
                {
                    g.DrawLine(line, size / 2f, y, size / 2f, y + h * 0.40f);
                    g.DrawLine(line, x, y + h * 0.40f, x + w, y + h * 0.40f);
                }
            }
            return bmp;
        }

        // Writes the green badge as a multi-size .ico. install.ps1 builds it into the .exe, so the
        // Start menu entry and Installed apps show the same icon as the tray.
        public static void SaveIconFile(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
            var images = new List<byte[]>();
            foreach (int size in sizes)
                using (Bitmap bmp = DrawBadge(ConnectedColor, size))
                    images.Add(size >= 256 ? PngBytes(bmp) : DibBytes(bmp));

            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length); // ICONDIR
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)                                // ICONDIRENTRY
                {
                    byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32); w.Write(images[i].Length); w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] image in images) w.Write(image);
            }
        }

        static byte[] PngBytes(Bitmap bmp)
        {
            using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
        }

        // 32-bit icon image: BITMAPINFOHEADER, bottom-up BGRA pixels, then an empty AND mask
        // (Windows uses the alpha channel for transparency).
        static byte[] DibBytes(Bitmap bmp)
        {
            int s = bmp.Width, maskBytes = (s + 31) / 32 * 4 * s;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
                w.Write(0); w.Write(s * s * 4 + maskBytes); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                for (int y = s - 1; y >= 0; y--)
                    for (int x = 0; x < s; x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                    }
                w.Write(new byte[maskBytes]);
                w.Flush();
                return ms.ToArray();
            }
        }

        static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
        {
            var path = new GraphicsPath();
            float d = Math.Min(r * 2, Math.Min(w, h));
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    // ------------------------------------------------------------- entry point
    static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int handle);

        [STAThread]
        static int Main(string[] args)
        {
            string arg = args.Length > 0 ? args[0].Trim().TrimStart('-', '/').ToLowerInvariant() : "";
            if (arg == "start") return StartFromShortcut();
            if (arg == "write-icon" && args.Length > 1)
            {
                // Used by install.ps1 at build time.
                try { TrayApp.SaveIconFile(args[1]); return 0; } catch { return 1; }
            }
            if (arg.Length > 0)
            {
                // This is a GUI program; borrow the caller's console for text output.
                IntPtr stdout = GetStdHandle(-11);
                if (stdout == IntPtr.Zero || stdout == new IntPtr(-1)) AttachConsole(-1);
                if (arg == "status") return PrintStatus();
                if (arg == "reconnect") return ManualReconnect();
                Console.WriteLine("MWB Keepalive. Usage: MWBKeepalive.exe [--start | --status | --reconnect]");
                return arg == "help" || arg == "?" || arg == "h" ? 0 : 2;
            }
            return RunTray();
        }

        // The Start menu entry: start it if it isn't running, otherwise say where it is.
        static int StartFromShortcut()
        {
            Application.EnableVisualStyles();
            if (IsRunning())
            {
                MessageBox.Show("MWB Keepalive is already running.\n\nIts icon (a round badge with a mouse) is in the " +
                    "notification area of the taskbar. Click it for the connection status.",
                    "MWB Keepalive", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            // Prefer the scheduled task, which restarts it if it ever crashes.
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/run /tn \"" + Config.TaskName + "\"")
                    { UseShellExecute = false, CreateNoWindow = true };
                using (Process p = Process.Start(psi))
                    if (p.WaitForExit(10000) && p.ExitCode == 0) return 0;
            }
            catch { }
            return RunTray(); // no scheduled task (installed via the Startup folder): run here
        }

        static bool IsRunning()
        {
            Mutex running;
            bool isRunning = Mutex.TryOpenExisting(Config.MutexName, out running);
            if (running != null) running.Dispose();
            return isRunning;
        }

        static int RunTray()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, Config.MutexName, out createdNew))
            {
                if (!createdNew) return 0; // already running
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        static int ManualReconnect()
        {
            try
            {
                string how = Mwb.Reconnect();
                Console.WriteLine("Reconnect triggered via " + how + ".");
                Log.Write("Manual reconnect triggered via " + how + ".");
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine("Reconnect failed: " + e.Message);
                return 1;
            }
        }

        static int PrintStatus()
        {
            MwbSettings s = MwbSettings.Load();
            if (s == null) { Console.WriteLine("MWB settings not found at " + Config.SettingsFile); return 1; }

            Console.WriteLine(string.Format("This PC: {0}   MWB running: {1}   MWB port: {2}",
                Environment.MachineName, Mwb.ProcessIds().Length > 0, s.MessagePort));
            var states = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try { states = Mwb.GetStates(); }
            catch (Exception e) { Console.WriteLine("Could not ask MWB for its state: " + e.Message); }
            Console.WriteLine("OS-level MWB connections: " + Net.MwbLinkCount(s.MessagePort));

            foreach (string peer in s.Peers)
            {
                int code;
                if (!states.TryGetValue(peer, out code)) code = 0;
                List<IPAddress> addresses;
                string via = "IP mapping";
                if (!s.Map.TryGetValue(peer, out addresses))
                {
                    via = "name lookup";
                    try { addresses = Dns.GetHostAddresses(peer).ToList(); } catch { addresses = new List<IPAddress>(); }
                }
                bool reachable = addresses.Count > 0 && Net.Knock(addresses, s.MessagePort);
                Console.WriteLine(string.Format("  {0,-16} MWB: {1,-12} reachable: {2,-5} via {3} {4}",
                    peer, Config.StatusName(code), reachable, via, string.Join(", ", addresses.Select(a => a.ToString()))));
            }

            Console.WriteLine("MWB Keepalive: " + (IsRunning() ? "running (tray icon)" : "not running"));
            return 0;
        }
    }
}
