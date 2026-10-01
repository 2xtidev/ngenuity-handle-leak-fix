// NGenuityLeakFix - closes the process handles that HyperX NGENUITY's
// NGenuity2Helper.exe opens and never closes, and shows what is happening in a
// tray icon and a small status window.
//
// Written against C# 5 on purpose: it has to compile with the csc.exe that
// ships with Windows (.NET Framework 4.x) and with Windows PowerShell 5.1's
// Add-Type, so nobody needs an SDK to build or audit it. The window is plain
// Windows Forms for the same reason.
//
// What it does, every few seconds:
//   1. Lists the handles NGenuity2Helper.exe holds (NtQuerySystemInformation,
//      SystemExtendedHandleInformation) and keeps only those of type Process.
//   2. Learns which process each new handle points to (a short-lived duplicate
//      into this process, GetProcessId, GetExitCodeProcess, then closed).
//   3. Once a handle is older than --min-age seconds it is closed inside the
//      helper (DuplicateHandle with DUPLICATE_CLOSE_SOURCE) if either
//        - the process it points to has exited, or
//        - the helper holds a newer handle to the same process.
//      The newest handle to every live process is always left alone.
//   4. Does nothing at all while the helper holds fewer than --threshold
//      process handles, so it goes quiet by itself if HyperX fixes the bug.
//   5. Measures the helper's CPU use and raises an alert when it has burned a
//      full core for several minutes (a separate helper bug it cannot fix).
//
// It never touches any other process and never touches handles of any other
// type. It needs no administrator rights: the helper runs as the logged-on
// user, and that user may duplicate handles out of their own processes.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NGenuityLeakFix
{
    public sealed class Options
    {
        public int IntervalSeconds = 5;
        public int MinAgeSeconds = 30;
        public int Threshold = 500;
        public bool DryRun = false;
        public bool Once = false;
        public bool Console = false;
        public bool Verbose = false;
        public bool NoUi = false;
        public string RenderPng = null;
        public int RenderAfterSeconds = 45;
        public int SpinCpuPercent = 90;
        public int SpinMinutes = 5;
        public string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NGenuityLeakFix", "NGenuityLeakFix.log");

        public bool Headless { get { return NoUi || Once || Console; } }

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                switch (a)
                {
                    case "--interval": o.IntervalSeconds = Math.Max(1, int.Parse(args[++i])); break;
                    case "--min-age": o.MinAgeSeconds = Math.Max(5, int.Parse(args[++i])); break;
                    case "--threshold": o.Threshold = Math.Max(0, int.Parse(args[++i])); break;
                    case "--log": o.LogPath = args[++i]; break;
                    case "--dry-run": o.DryRun = true; break;
                    case "--once": o.Once = true; break;
                    case "--console": o.Console = true; break;
                    case "--verbose": o.Verbose = true; break;
                    case "--no-ui": o.NoUi = true; break;
                    case "--spin-cpu": o.SpinCpuPercent = Math.Max(0, int.Parse(args[++i])); break;
                    case "--spin-minutes": o.SpinMinutes = Math.Max(1, int.Parse(args[++i])); break;
                    case "--render-png": o.RenderPng = args[++i]; break;
                    case "--render-after": o.RenderAfterSeconds = Math.Max(5, int.Parse(args[++i])); break;
                    default: throw new ArgumentException("Unknown argument: " + args[i]);
                }
            }
            return o;
        }
    }

    public sealed class TickResult
    {
        public int HelperPid;
        public DateTime HelperStartUtc;
        public int ProcessHandles;
        public int TotalHandles;
        public int ExitedTargets;
        public int LiveTargets;
        public double HelperCpuPercent = -1;
        public int Closed;
        public int WouldClose;
        public int ClosedExited;
        public int ClosedDuplicate;
        public int Failed;
        public string Note;
    }

    public sealed class Fixer
    {
        const string HelperExe = "NGenuity2Helper.exe";

        sealed class Entry
        {
            public DateTime FirstSeen;
            public int TargetPid;
            public bool Exited;
        }

        readonly Options opt;
        readonly int processTypeIndex;
        readonly Dictionary<long, Entry> entries = new Dictionary<long, Entry>();
        int trackedPid;
        DateTime trackedStart;
        bool firstPass;
        long lastCpuTicks = -1;
        DateTime lastCpuAt;
        IntPtr helper = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        int bufferSize = 16 * 1024 * 1024;

        public bool DryRun;

        public Fixer(Options options)
        {
            opt = options;
            DryRun = options.DryRun;
            processTypeIndex = FindProcessTypeIndex();
        }

        public TickResult Tick()
        {
            var r = new TickResult();
            int pid = FindHelperPid();
            if (pid == 0)
            {
                Reset();
                r.Note = HelperExe + " is not running";
                return r;
            }
            // A restarted helper can get its old pid back, so compare the
            // start time too: handle values from the previous instance mean
            // nothing in the new one.
            DateTime started = HelperStartUtc(pid, DateTime.MinValue);
            if (pid != trackedPid || started != trackedStart)
            {
                Reset();
                helper = Native.OpenProcess(Native.PROCESS_DUP_HANDLE | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (helper == IntPtr.Zero)
                {
                    r.Note = "OpenProcess(" + pid + ") failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return r;
                }
                trackedPid = pid;
                trackedStart = started;
                firstPass = true;
            }
            r.HelperPid = pid;
            r.HelperStartUtc = started;
            r.HelperCpuPercent = MeasureCpu();

            var current = SnapshotProcessHandles(pid, out r.TotalHandles);
            r.ProcessHandles = current.Count;

            var gone = new List<long>();
            foreach (var h in entries.Keys) if (!current.Contains(h)) gone.Add(h);
            foreach (var h in gone) entries.Remove(h);

            // Handles found on the first pass after attaching have been open
            // for an unknown time, at most since the helper started.
            DateTime now = DateTime.UtcNow;
            DateTime seen = firstPass && started != DateTime.MinValue ? started : now;
            firstPass = false;
            foreach (var h in current)
            {
                if (entries.ContainsKey(h)) continue;
                var e = new Entry { FirstSeen = seen };
                Inspect(h, e);
                entries[h] = e;
            }

            if (r.ProcessHandles < opt.Threshold)
            {
                CountTargets(r);
                r.Note = "below threshold (" + opt.Threshold + "), nothing to do";
                return r;
            }

            DateTime cutoff = now.AddSeconds(-opt.MinAgeSeconds);

            // Exit status can change after a handle was first seen: refresh it
            // for every handle that is old enough to be a candidate.
            foreach (var kv in entries)
                if (!kv.Value.Exited && kv.Value.FirstSeen <= cutoff) Inspect(kv.Key, kv.Value);

            CountTargets(r);

            // The newest handle per live target process is always kept.
            var newest = new Dictionary<int, long>();
            foreach (var kv in entries)
            {
                if (kv.Value.Exited || kv.Value.TargetPid == 0) continue;
                long best;
                if (!newest.TryGetValue(kv.Value.TargetPid, out best) || IsNewer(kv.Key, kv.Value, best, entries[best]))
                    newest[kv.Value.TargetPid] = kv.Key;
            }

            var toClose = new List<long>();
            foreach (var kv in entries)
            {
                var e = kv.Value;
                if (e.FirstSeen > cutoff) continue;
                if (e.Exited) { toClose.Add(kv.Key); r.ClosedExited++; continue; }
                if (e.TargetPid == 0) continue;
                if (newest[e.TargetPid] != kv.Key) { toClose.Add(kv.Key); r.ClosedDuplicate++; }
            }

            foreach (var h in toClose)
            {
                if (DryRun) { r.WouldClose++; continue; }
                IntPtr ignored;
                if (Native.DuplicateHandle(helper, new IntPtr(h), IntPtr.Zero, out ignored, 0, false, Native.DUPLICATE_CLOSE_SOURCE))
                {
                    r.Closed++;
                    entries.Remove(h);
                }
                else r.Failed++;
            }
            if (DryRun) r.Note = "dry run, nothing was closed";
            return r;
        }

        public bool KillHelper()
        {
            if (trackedPid == 0) return false;
            try { using (var p = Process.GetProcessById(trackedPid)) p.Kill(); return true; }
            catch { return false; }
        }

        void CountTargets(TickResult r)
        {
            var exited = new HashSet<int>();
            var live = new HashSet<int>();
            foreach (var e in entries.Values)
            {
                if (e.TargetPid == 0) continue;
                if (e.Exited) exited.Add(e.TargetPid); else live.Add(e.TargetPid);
            }
            r.ExitedTargets = exited.Count;
            r.LiveTargets = live.Count;
        }

        // Share of one CPU core the helper used since the previous pass.
        double MeasureCpu()
        {
            long creation, exit, kernel, user;
            if (!Native.GetProcessTimes(helper, out creation, out exit, out kernel, out user)) return -1;
            long ticks = kernel + user;
            DateTime now = DateTime.UtcNow;
            double pct = -1;
            if (lastCpuTicks >= 0)
            {
                double wall = (now - lastCpuAt).TotalSeconds;
                if (wall > 0.5) pct = Math.Max(0, (ticks - lastCpuTicks) / 1e7 / wall * 100.0);
            }
            lastCpuTicks = ticks;
            lastCpuAt = now;
            return pct;
        }

        static DateTime HelperStartUtc(int pid, DateTime fallback)
        {
            try { using (var p = Process.GetProcessById(pid)) return p.StartTime.ToUniversalTime(); }
            catch { return fallback; }
        }

        static bool IsNewer(long h, Entry e, long otherH, Entry other)
        {
            if (e.FirstSeen != other.FirstSeen) return e.FirstSeen > other.FirstSeen;
            return h > otherH;
        }

        void Inspect(long handle, Entry e)
        {
            IntPtr dup;
            if (!Native.DuplicateHandle(helper, new IntPtr(handle), Native.GetCurrentProcess(), out dup,
                    Native.PROCESS_QUERY_LIMITED_INFORMATION, false, 0))
                return;
            try
            {
                e.TargetPid = Native.GetProcessId(dup);
                int code;
                if (Native.GetExitCodeProcess(dup, out code) && code != Native.STILL_ACTIVE) e.Exited = true;
            }
            finally { Native.CloseHandle(dup); }
        }

        HashSet<long> SnapshotProcessHandles(int pid, out int totalHandles)
        {
            totalHandles = 0;
            QuerySystemHandles();
            var set = new HashSet<long>();
            long n = Marshal.ReadInt64(buffer);
            for (long i = 0; i < n; i++)
            {
                IntPtr e = new IntPtr(buffer.ToInt64() + Native.HeaderSize + i * Native.EntrySize);
                if (Marshal.ReadInt64(e, 8) != pid) continue;
                totalHandles++;
                if ((Marshal.ReadInt16(e, 30) & 0xFFFF) != processTypeIndex) continue;
                set.Add(Marshal.ReadInt64(e, 16));
            }
            return set;
        }

        void QuerySystemHandles()
        {
            while (true)
            {
                if (buffer == IntPtr.Zero) buffer = Marshal.AllocHGlobal(bufferSize);
                int needed;
                int status = Native.NtQuerySystemInformation(Native.SystemExtendedHandleInformation, buffer, bufferSize, out needed);
                if (status == 0) return;
                if (status != Native.STATUS_INFO_LENGTH_MISMATCH)
                    throw new InvalidOperationException("NtQuerySystemInformation failed: 0x" + status.ToString("X8"));
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                bufferSize = Math.Max(bufferSize * 2, needed + 1024 * 1024);
            }
        }

        // The index of the "Process" object type differs between Windows
        // builds, so learn it from a process handle we open ourselves.
        int FindProcessTypeIndex()
        {
            IntPtr self = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, Process.GetCurrentProcess().Id);
            try
            {
                QuerySystemHandles();
                long me = Process.GetCurrentProcess().Id;
                long n = Marshal.ReadInt64(buffer);
                for (long i = 0; i < n; i++)
                {
                    IntPtr e = new IntPtr(buffer.ToInt64() + Native.HeaderSize + i * Native.EntrySize);
                    if (Marshal.ReadInt64(e, 8) == me && Marshal.ReadInt64(e, 16) == self.ToInt64())
                        return Marshal.ReadInt16(e, 30) & 0xFFFF;
                }
                throw new InvalidOperationException("Could not determine the Process object type index");
            }
            finally { Native.CloseHandle(self); }
        }

        static int FindHelperPid()
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(HelperExe)))
            {
                try
                {
                    IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        var sb = new StringBuilder(32768);
                        int size = sb.Capacity;
                        if (Native.QueryFullProcessImageName(h, 0, sb, ref size) &&
                            Path.GetFileName(sb.ToString()).Equals(HelperExe, StringComparison.OrdinalIgnoreCase))
                            return p.Id;
                    }
                    finally { Native.CloseHandle(h); }
                }
                finally { p.Dispose(); }
            }
            return 0;
        }

        void Reset()
        {
            entries.Clear();
            trackedPid = 0;
            trackedStart = DateTime.MinValue;
            lastCpuTicks = -1;
            if (helper != IntPtr.Zero) { Native.CloseHandle(helper); helper = IntPtr.Zero; }
        }
    }

    public static class Logger
    {
        static readonly object gate = new object();
        static readonly List<string> tail = new List<string>();
        static long version;

        public static void Write(Options opt, string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message;
            if (opt.Console) System.Console.WriteLine(line);
            lock (gate)
            {
                tail.Add(line);
                if (tail.Count > 200) tail.RemoveAt(0);
                version++;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(opt.LogPath));
                    var fi = new FileInfo(opt.LogPath);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        string old = opt.LogPath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(opt.LogPath, old);
                    }
                    File.AppendAllText(opt.LogPath, line + Environment.NewLine);
                }
                catch { }
            }
        }

        public static string[] Tail(out long tailVersion)
        {
            lock (gate) { tailVersion = version; return tail.ToArray(); }
        }
    }

    public struct Sample
    {
        public DateTime Time;
        public int Handles;
        public int Closed;
    }

    public sealed class Snapshot
    {
        public DateTime Started;
        public DateTime LastTick;
        public bool HelperRunning;
        public int HelperPid;
        public DateTime HelperStartUtc;
        public int ProcessHandles;
        public int TotalHandles;
        public int ExitedTargets;
        public int LiveTargets;
        public double HelperCpu = -1;
        public long TotalClosed;
        public int ClosedLastMinute;
        public bool Paused;
        public bool DryRun;
        public bool Spinning;
        public bool BelowThreshold;
        public string Error;
        public Sample[] Samples = new Sample[0];
    }

    // Runs the fixer on its own thread and keeps the numbers the UI shows.
    public sealed class Engine
    {
        const int HistoryMinutes = 60;

        readonly Options opt;
        readonly object gate = new object();
        readonly List<Sample> samples = new List<Sample>();
        Fixer fixer;
        Thread thread;
        volatile bool stop;
        volatile bool paused;
        volatile bool killRequested;

        TickResult last;
        DateTime lastTick;
        readonly DateTime started = DateTime.Now;
        long totalClosed;
        bool spinning;
        DateTime highCpuSince = DateTime.MinValue;
        string error;

        long closedSinceReport;
        DateTime lastReport = DateTime.UtcNow;
        string lastNote;

        public Engine(Options options) { opt = options; }

        public bool Paused { get { return paused; } set { paused = value; } }

        public bool Init()
        {
            try { fixer = new Fixer(opt); }
            catch (Exception ex)
            {
                error = ex.Message;
                Logger.Write(opt, "Cannot start: " + ex.Message);
                return false;
            }
            Logger.Write(opt, "Started (interval " + opt.IntervalSeconds + "s, min-age " + opt.MinAgeSeconds +
                              "s, threshold " + opt.Threshold + (opt.DryRun ? ", DRY RUN" : "") + ").");
            return true;
        }

        public void Start()
        {
            thread = new Thread(RunLoop) { IsBackground = true, Name = "NGenuityLeakFix engine" };
            thread.Start();
        }

        public void Stop() { stop = true; }

        public void RequestKillHelper() { killRequested = true; }

        public void RunLoop()
        {
            while (!stop)
            {
                Step();
                if (opt.Once) return;
                for (int i = 0; i < opt.IntervalSeconds * 4 && !stop && !killRequested; i++) Thread.Sleep(250);
            }
        }

        void Step()
        {
            if (killRequested)
            {
                killRequested = false;
                Logger.Write(opt, fixer.KillHelper() ? "Helper process ended on request." : "Could not end the helper process.");
                Thread.Sleep(500);
            }

            TickResult r;
            fixer.DryRun = opt.DryRun || paused;
            try { r = fixer.Tick(); }
            catch (Exception ex)
            {
                lock (gate) error = ex.Message;
                Logger.Write(opt, "Error: " + ex.Message);
                return;
            }

            bool spinStarted = false, spinEnded = false;
            lock (gate)
            {
                error = null;
                last = r;
                lastTick = DateTime.Now;
                totalClosed += r.Closed;
                samples.Add(new Sample { Time = lastTick, Handles = r.HelperPid == 0 ? 0 : r.ProcessHandles, Closed = r.Closed });
                DateTime oldest = lastTick.AddMinutes(-HistoryMinutes);
                int drop = 0;
                while (drop < samples.Count && samples[drop].Time < oldest) drop++;
                if (drop > 0) samples.RemoveRange(0, drop);

                if (r.HelperPid != 0 && r.HelperCpuPercent >= opt.SpinCpuPercent)
                {
                    if (highCpuSince == DateTime.MinValue) highCpuSince = DateTime.UtcNow;
                    else if (!spinning && (DateTime.UtcNow - highCpuSince).TotalMinutes >= opt.SpinMinutes) { spinning = true; spinStarted = true; }
                }
                else if (r.HelperPid == 0 || r.HelperCpuPercent >= 0)
                {
                    highCpuSince = DateTime.MinValue;
                    if (spinning) { spinning = false; spinEnded = true; }
                }
            }
            if (spinStarted)
                Logger.Write(opt, "pid " + r.HelperPid + ": the helper has used a full CPU core for " + opt.SpinMinutes +
                                  " minutes. This tool cannot fix that; end the helper or restart NGENUITY.");
            if (spinEnded) Logger.Write(opt, "The helper is no longer using a full CPU core.");

            // A pass that closes a backlog is logged on its own and kept out
            // of the ten-minute summary.
            bool big = r.Closed >= 1000;
            if (!big) closedSinceReport += r.Closed;
            bool noteChanged = r.Note != lastNote;
            if (noteChanged) FlushSummary(r);
            if (opt.Verbose || opt.Once || big) Logger.Write(opt, Describe(r));
            else if (noteChanged && r.Closed == 0) Logger.Write(opt, Describe(r));
            else if ((DateTime.UtcNow - lastReport).TotalSeconds >= 600) FlushSummary(r);
            lastNote = r.Note;
        }

        void FlushSummary(TickResult r)
        {
            if (closedSinceReport > 0)
            {
                string minutes = Math.Max(1, (int)Math.Round((DateTime.UtcNow - lastReport).TotalMinutes)).ToString();
                Logger.Write(opt, (r.HelperPid != 0 ? "pid " + r.HelperPid + ": " + r.ProcessHandles + " process handles, " : "") +
                                  "closed " + closedSinceReport + " in the last " + minutes + " minutes");
            }
            closedSinceReport = 0;
            lastReport = DateTime.UtcNow;
        }

        static string Describe(TickResult r)
        {
            if (r.HelperPid == 0) return r.Note;
            var s = "pid " + r.HelperPid + ": " + r.TotalHandles + " handles, " + r.ProcessHandles + " of type Process";
            int n = r.Closed + r.WouldClose;
            if (n > 0 || r.Failed > 0)
                s += "; closed " + n + " (" + r.ClosedDuplicate + " duplicate, " + r.ClosedExited + " to exited processes)" +
                     (r.Failed > 0 ? ", failed " + r.Failed : "");
            if (r.Note != null) s += "; " + r.Note;
            return s;
        }

        public Snapshot GetSnapshot()
        {
            lock (gate)
            {
                var s = new Snapshot { Started = started, LastTick = lastTick, Paused = paused, DryRun = opt.DryRun, Spinning = spinning, Error = error, TotalClosed = totalClosed };
                if (last != null)
                {
                    s.HelperRunning = last.HelperPid != 0;
                    s.HelperPid = last.HelperPid;
                    s.HelperStartUtc = last.HelperStartUtc;
                    s.ProcessHandles = last.ProcessHandles;
                    s.TotalHandles = last.TotalHandles;
                    s.ExitedTargets = last.ExitedTargets;
                    s.LiveTargets = last.LiveTargets;
                    s.HelperCpu = last.HelperCpuPercent;
                    s.BelowThreshold = s.HelperRunning && last.ProcessHandles < opt.Threshold;
                }
                s.Samples = samples.ToArray();
                DateTime minuteAgo = DateTime.Now.AddSeconds(-60);
                for (int i = s.Samples.Length - 1; i >= 0 && s.Samples[i].Time >= minuteAgo; i--) s.ClosedLastMinute += s.Samples[i].Closed;
                return s;
            }
        }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Options opt;
            try { opt = Options.Parse(args); }
            catch (Exception ex)
            {
                Native.AttachOrAllocConsole();
                System.Console.Error.WriteLine(ex.Message + "\n\n" + Usage);
                return 2;
            }
            return Run(opt);
        }

        public const string Usage =
            "NGenuityLeakFix [--interval 5] [--min-age 30] [--threshold 500] [--spin-cpu 90] [--spin-minutes 5] [--no-ui] [--dry-run] [--once] [--console] [--verbose] [--log path]";

        public static int Run(Options opt)
        {
            if (opt.Console) Native.AttachOrAllocConsole();
            if (IntPtr.Size != 8) { Logger.Write(opt, "This tool only supports 64-bit Windows."); return 1; }

            bool created;
            using (var mutex = new Mutex(true, @"Local\NGenuityLeakFix", out created))
            {
                // Two instances closing handles in the same helper could race
                // on a reused handle value, so only a dry run may run alongside
                // an installed instance.
                if (!created && !(opt.Once && opt.DryRun)) { Logger.Write(opt, "Another instance is already running, exiting."); return 0; }

                var engine = new Engine(opt);
                if (!engine.Init()) return 1;

                if (opt.Headless) { engine.RunLoop(); return 0; }

                Native.SetProcessDPIAware();
                try { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); }
                catch (InvalidOperationException) { }
                engine.Start();
                using (var tray = new TrayApp(engine, opt)) Application.Run(tray);
                engine.Stop();
                return 0;
            }
        }
    }

    enum Health { Ok, Idle, Warning, Problem }

    static class Ui
    {
        public static readonly Color Green = Color.FromArgb(46, 160, 67);
        public static readonly Color Gray = Color.FromArgb(140, 149, 159);
        public static readonly Color Amber = Color.FromArgb(210, 153, 34);
        public static readonly Color Red = Color.FromArgb(207, 34, 46);
        public static readonly Color Line = Color.FromArgb(9, 105, 218);

        public static Color ColorOf(Health h)
        {
            return h == Health.Ok ? Green : h == Health.Idle ? Gray : h == Health.Warning ? Amber : Red;
        }

        public static Health HealthOf(Snapshot s, out string headline, out string detail)
        {
            if (s.Error != null) { headline = "Cannot read the helper's handles"; detail = s.Error; return Health.Problem; }
            if (s.LastTick == DateTime.MinValue) { headline = "Starting"; detail = "Waiting for the first pass."; return Health.Idle; }
            if (!s.HelperRunning) { headline = "NGENUITY helper is not running"; detail = "Nothing to do until NGENUITY starts."; return Health.Idle; }
            if (s.Spinning)
            {
                headline = "The helper is using a full CPU core";
                detail = "A separate NGENUITY bug. End the helper below, or restart NGENUITY.";
                return Health.Problem;
            }
            if (s.Paused || s.DryRun)
            {
                headline = s.Paused ? "Paused: watching only" : "Dry run: watching only";
                detail = "Leaked handles are counted but not closed.";
                return Health.Warning;
            }
            if (s.BelowThreshold) { headline = "The helper is not leaking right now"; detail = "Fewer handles than the threshold; nothing is being closed."; return Health.Ok; }
            headline = "Leak under control";
            detail = "Leaked handles are closed as they turn 30 seconds old.";
            return Health.Ok;
        }

        public static Icon MakeIcon(Color color)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var b = new SolidBrush(color)) g.FillEllipse(b, 3, 3, 26, 26);
                    using (var p = new Pen(Color.FromArgb(90, 0, 0, 0), 1.5f)) g.DrawEllipse(p, 3, 3, 26, 26);
                    using (var p = new Pen(Color.White, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(p, new[] { new PointF(11, 21), new PointF(11, 11), new PointF(21, 21), new PointF(21, 11) });
                }
                IntPtr h = bmp.GetHicon();
                try { using (var tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone(); }
                finally { Native.DestroyIcon(h); }
            }
        }

        public static string Duration(TimeSpan t)
        {
            if (t.TotalDays >= 1) return (int)t.TotalDays + "d " + t.Hours + "h";
            if (t.TotalHours >= 1) return (int)t.TotalHours + "h " + t.Minutes + "m";
            if (t.TotalMinutes >= 1) return (int)t.TotalMinutes + "m";
            return (int)t.TotalSeconds + "s";
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        readonly Engine engine;
        readonly Options opt;
        readonly NotifyIcon notify;
        readonly Dictionary<Health, Icon> icons = new Dictionary<Health, Icon>();
        readonly System.Windows.Forms.Timer timer;
        readonly ToolStripMenuItem pauseItem;
        readonly ToolStripMenuItem endItem;
        StatusForm form;
        Health shown = (Health)(-1);
        bool spinAnnounced;
        int renderCountdown;

        public TrayApp(Engine engine, Options opt)
        {
            this.engine = engine;
            this.opt = opt;
            renderCountdown = opt.RenderAfterSeconds;
            foreach (Health h in Enum.GetValues(typeof(Health))) icons[h] = Ui.MakeIcon(Ui.ColorOf(h));

            var menu = new ContextMenuStrip();
            var open = new ToolStripMenuItem("Open status window", null, delegate { ShowForm(); });
            open.Font = new Font(open.Font, FontStyle.Bold);
            pauseItem = new ToolStripMenuItem("Pause", null, delegate { TogglePause(); });
            endItem = new ToolStripMenuItem("End helper process", null, delegate { EndHelper(); });
            menu.Items.Add(open);
            menu.Items.Add(pauseItem);
            menu.Items.Add(endItem);
            menu.Items.Add(new ToolStripMenuItem("Open log", null, delegate { OpenLog(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { ExitThread(); }));

            notify = new NotifyIcon { Icon = icons[Health.Idle], Text = "NGenuityLeakFix", ContextMenuStrip = menu, Visible = true };
            notify.DoubleClick += delegate { ShowForm(); };
            notify.BalloonTipClicked += delegate { ShowForm(); };

            timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += delegate { Refresh(); };
            timer.Start();
            if (opt.RenderPng != null) ShowForm();
            Refresh();
        }

        public void TogglePause() { engine.Paused = !engine.Paused; Refresh(); }

        public void EndHelper()
        {
            if (MessageBox.Show("End NGenuity2Helper.exe?\n\nNGENUITY starts a new one the next time the app is opened. Device settings are not affected.",
                    "NGenuityLeakFix", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                engine.RequestKillHelper();
        }

        public void OpenLog()
        {
            try { Process.Start(opt.LogPath); }
            catch { try { Process.Start(Path.GetDirectoryName(opt.LogPath)); } catch { } }
        }

        void ShowForm()
        {
            if (form == null || form.IsDisposed) form = new StatusForm(this, engine, icons[Health.Ok]);
            form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
            form.Render(engine.GetSnapshot());
        }

        void Refresh()
        {
            Snapshot s = engine.GetSnapshot();
            string headline, detail;
            Health h = Ui.HealthOf(s, out headline, out detail);
            if (h != shown) { notify.Icon = icons[h]; shown = h; }

            string tip = !s.HelperRunning ? "NGenuityLeakFix: " + headline
                : "NGenuityLeakFix: " + s.ProcessHandles.ToString("N0") + " handles held, " + s.ClosedLastMinute.ToString("N0") + " closed/min";
            notify.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            pauseItem.Text = s.Paused ? "Resume" : "Pause";
            endItem.Enabled = s.HelperRunning;

            if (s.Spinning && !spinAnnounced)
            {
                spinAnnounced = true;
                notify.ShowBalloonTip(15000, "NGENUITY helper is using a full CPU core",
                    "It has done so for several minutes. Click to open NGenuityLeakFix and end the helper.", ToolTipIcon.Warning);
            }
            else if (!s.Spinning) spinAnnounced = false;

            if (form != null && !form.IsDisposed && form.Visible) form.Render(s);

            if (opt.RenderPng != null && --renderCountdown <= 0)
            {
                timer.Stop();
                try
                {
                    // The client area only: DrawToBitmap paints an old-style title bar.
                    using (var bmp = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                        Point origin = form.PointToScreen(Point.Empty);
                        var client = new Rectangle(origin.X - form.Left, origin.Y - form.Top, form.ClientSize.Width, form.ClientSize.Height);
                        using (var cropped = bmp.Clone(client, bmp.PixelFormat))
                            cropped.Save(opt.RenderPng, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                catch (Exception ex) { Logger.Write(opt, "Render failed: " + ex.Message); }
                ExitThread();
            }
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            notify.Visible = false;
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                notify.Dispose();
                foreach (var i in icons.Values) i.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    sealed class StatusForm : Form
    {
        readonly TrayApp app;
        readonly Panel dot = new Panel();
        readonly Label headline = new Label();
        readonly Label detail = new Label();
        readonly Label[] values = new Label[6];
        readonly ChartControl chart = new ChartControl();
        readonly TextBox log = new TextBox();
        readonly Button pause = new Button();
        readonly Button end = new Button();
        readonly ToolTip tips = new ToolTip();
        Color dotColor = Ui.Gray;
        long logVersion = -1;

        static readonly string[] Captions =
        {
            "Handles held by the helper", "Closed in the last minute", "Closed since this tool started",
            "Exited processes kept alive", "Helper CPU", "Helper process"
        };

        static readonly string[] Hints =
        {
            "Process handles NGenuity2Helper.exe holds right now. A few thousand is the steady state with the fix running.",
            "Leaked handles closed during the last 60 seconds. This is the leak rate.",
            "Total closed since NGenuityLeakFix started.",
            "Processes that have exited but are kept in the kernel because the helper still holds a handle to them.",
            "Share of one CPU core the helper is using. A constant 100% is the CPU-spin bug.",
            "Process id and how long the helper has been running."
        };

        public StatusForm(TrayApp app, Engine engine, Icon icon)
        {
            this.app = app;
            Text = "NGenuityLeakFix";
            Icon = icon;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = SystemColors.Window;
            ClientSize = new Size(560, 600);
            Padding = new Padding(16);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var head = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            dot.Size = new Size(18, 18);
            dot.Margin = new Padding(0, 6, 0, 0);
            dot.Paint += delegate(object o, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(dotColor)) e.Graphics.FillEllipse(b, 1, 1, 15, 15);
            };
            headline.AutoSize = true;
            headline.Font = new Font("Segoe UI Semibold", 13f);
            headline.Margin = new Padding(0);
            detail.AutoSize = true;
            detail.ForeColor = SystemColors.GrayText;
            detail.Margin = new Padding(2, 2, 0, 0);
            head.Controls.Add(dot, 0, 0);
            head.Controls.Add(headline, 1, 0);
            head.Controls.Add(detail, 1, 1);
            root.Controls.Add(head, 0, 0);

            var stats = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
            for (int c = 0; c < 3; c++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            for (int i = 0; i < 6; i++)
            {
                var cell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, AutoSize = true, Margin = new Padding(0, 0, 8, 10) };
                var caption = new Label { Text = Captions[i], AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0) };
                values[i] = new Label { Text = "-", AutoSize = true, Font = new Font("Segoe UI Semibold", 14f), Margin = new Padding(0, 1, 0, 0) };
                tips.SetToolTip(caption, Hints[i]);
                tips.SetToolTip(values[i], Hints[i]);
                cell.Controls.Add(caption, 0, 0);
                cell.Controls.Add(values[i], 0, 1);
                stats.Controls.Add(cell, i % 3, i / 3);
            }
            root.Controls.Add(stats, 0, 1);

            chart.Dock = DockStyle.Fill;
            chart.Margin = new Padding(0, 0, 0, 12);
            root.Controls.Add(chart, 0, 2);

            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.WordWrap = true;
            log.BorderStyle = BorderStyle.FixedSingle;
            log.BackColor = SystemColors.Window;
            log.Font = new Font("Consolas", 8.25f);
            log.Dock = DockStyle.Fill;
            log.Margin = new Padding(0, 0, 0, 12);
            log.TabStop = false;
            root.Controls.Add(log, 0, 3);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
            pause.Text = "Pause";
            pause.AutoSize = true;
            pause.MinimumSize = new Size(96, 30);
            pause.Margin = new Padding(0, 0, 8, 0);
            pause.Click += delegate { app.TogglePause(); };
            end.Text = "End helper process";
            end.AutoSize = true;
            end.MinimumSize = new Size(96, 30);
            end.Margin = new Padding(0, 0, 8, 0);
            end.Click += delegate { app.EndHelper(); };
            var openLog = new Button { Text = "Open log", AutoSize = true, MinimumSize = new Size(96, 30), Margin = new Padding(0) };
            openLog.Click += delegate { app.OpenLog(); };
            tips.SetToolTip(pause, "Keep watching but stop closing handles.");
            tips.SetToolTip(end, "Stops NGenuity2Helper.exe. Use it when the helper is stuck at 100% CPU.");
            buttons.Controls.Add(pause);
            buttons.Controls.Add(end);
            buttons.Controls.Add(openLog);
            root.Controls.Add(buttons, 0, 4);
        }

        public void Render(Snapshot s)
        {
            string h, d;
            Health health = Ui.HealthOf(s, out h, out d);
            headline.Text = h;
            detail.Text = d;
            Color c = Ui.ColorOf(health);
            if (c != dotColor) { dotColor = c; dot.Invalidate(); }

            values[0].Text = s.HelperRunning ? s.ProcessHandles.ToString("N0") : "-";
            values[1].Text = s.ClosedLastMinute.ToString("N0");
            values[2].Text = s.TotalClosed.ToString("N0");
            values[3].Text = s.HelperRunning ? s.ExitedTargets.ToString("N0") : "-";
            values[4].Text = s.HelperRunning && s.HelperCpu >= 0 ? Math.Round(s.HelperCpu) + "%" : "-";
            values[4].ForeColor = s.Spinning ? Ui.Red : SystemColors.ControlText;
            values[5].Text = s.HelperRunning
                ? "pid " + s.HelperPid + (s.HelperStartUtc != DateTime.MinValue ? ", up " + Ui.Duration(DateTime.UtcNow - s.HelperStartUtc) : "")
                : "not running";

            pause.Text = s.Paused ? "Resume" : "Pause";
            end.Enabled = s.HelperRunning;
            chart.SetSamples(s.Samples);

            long v;
            string[] lines = Logger.Tail(out v);
            if (v != logVersion)
            {
                logVersion = v;
                int from = Math.Max(0, lines.Length - 60);
                log.Text = string.Join(Environment.NewLine, lines, from, lines.Length - from);
                // Caret at the start of the last line: scrolls to the bottom
                // without scrolling sideways.
                log.SelectionStart = Math.Max(0, log.Text.LastIndexOf('\n') + 1);
                log.ScrollToCaret();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Closing the window keeps the tool running in the tray.
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            base.OnFormClosing(e);
        }
    }

    // Last hour: handles held by the helper (line) and handles closed per pass (bars).
    sealed class ChartControl : Control
    {
        Sample[] samples = new Sample[0];

        public ChartControl()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void SetSamples(Sample[] s) { samples = s; Invalidate(); }

        static int NiceMax(int v)
        {
            if (v <= 10) return 10;
            double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
            double f = v / p;
            double n = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
            return (int)(n * p);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(SystemColors.Window);
            float scale = g.DpiX / 96f;
            var gray = SystemColors.GrayText;
            int labelH = (int)Math.Ceiling(Font.GetHeight(g));
            var plot = new RectangleF(1, labelH + 6 * scale, Width - 2, Height - 2 * labelH - 14 * scale);
            if (plot.Width < 20 || plot.Height < 20) return;

            int maxHandles = 0;
            var closed = new List<int>();
            foreach (var s in samples) { if (s.Handles > maxHandles) maxHandles = s.Handles; if (s.Closed > 0) closed.Add(s.Closed); }
            maxHandles = NiceMax(maxHandles);
            // One huge clean-up pass would flatten every other bar, so scale
            // the bars to the 95th percentile and let outliers clip.
            int maxClosed = 10;
            if (closed.Count > 0) { closed.Sort(); maxClosed = NiceMax(closed[(int)((closed.Count - 1) * 0.95)]); }

            using (var grid = new Pen(Color.FromArgb(40, gray)))
                for (int i = 0; i <= 4; i++)
                {
                    float y = plot.Top + plot.Height * i / 4f;
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                }

            // Show the history there is, between five minutes and an hour.
            DateTime now = DateTime.Now;
            double span = 300;
            if (samples.Length > 0) span = Math.Min(3600, Math.Max(300, (now - samples[0].Time).TotalSeconds));
            int spanMinutes = (int)Math.Round(span / 60);
            Func<DateTime, float> X = delegate(DateTime t) { return plot.Right - (float)((now - t).TotalSeconds / span) * plot.Width; };

            using (var bar = new Pen(Color.FromArgb(110, Ui.Green), Math.Max(1f, scale)))
                foreach (var s in samples)
                {
                    if (s.Closed <= 0) continue;
                    float x = X(s.Time);
                    if (x < plot.Left) continue;
                    float hgt = Math.Min(1f, s.Closed / (float)maxClosed) * plot.Height;
                    g.DrawLine(bar, x, plot.Bottom, x, plot.Bottom - hgt);
                }

            var pts = new List<PointF>();
            foreach (var s in samples)
            {
                float x = X(s.Time);
                if (x < plot.Left) continue;
                pts.Add(new PointF(x, plot.Bottom - Math.Min(1f, s.Handles / (float)maxHandles) * plot.Height));
            }
            if (pts.Count > 1) using (var line = new Pen(Ui.Line, 2f * scale) { LineJoin = LineJoin.Round }) g.DrawLines(line, pts.ToArray());

            using (var text = new SolidBrush(gray))
            using (var blue = new SolidBrush(Ui.Line))
            using (var green = new SolidBrush(Ui.Green))
            using (var right = new StringFormat { Alignment = StringAlignment.Far })
            {
                g.DrawString("Handles held (max " + maxHandles.ToString("N0") + ")", Font, blue, plot.Left, 0);
                g.DrawString("Closed per pass (max " + maxClosed.ToString("N0") + ")", Font, green, plot.Right, 0, right);
                g.DrawString(spanMinutes + " min ago", Font, text, plot.Left, plot.Bottom + 4 * scale);
                g.DrawString("now", Font, text, plot.Right, plot.Bottom + 4 * scale, right);
                if (pts.Count < 2)
                    using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString("Collecting data", Font, text, plot, center);
            }
        }
    }

    static class Native
    {
        public const int SystemExtendedHandleInformation = 64;
        public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
        public const int PROCESS_DUP_HANDLE = 0x0040;
        public const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const int DUPLICATE_CLOSE_SOURCE = 0x1;
        public const int STILL_ACTIVE = 259;

        // SYSTEM_HANDLE_INFORMATION_EX on x64: NumberOfHandles + Reserved,
        // then SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX entries of 40 bytes:
        //   0 Object, 8 UniqueProcessId, 16 HandleValue, 24 GrantedAccess,
        //   28 CreatorBackTraceIndex, 30 ObjectTypeIndex, 32 HandleAttributes.
        public const int HeaderSize = 16;
        public const int EntrySize = 40;

        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
            out IntPtr targetHandle, int access, bool inherit, int options);

        [DllImport("kernel32.dll")]
        public static extern int GetProcessId(IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr process, out int exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr icon);

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int pid);

        [DllImport("kernel32.dll")]
        static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        public static void AttachOrAllocConsole()
        {
            if (GetConsoleWindow() != IntPtr.Zero) return;
            if (!AttachConsole(-1)) AllocConsole();
            var stdout = new StreamWriter(System.Console.OpenStandardOutput()) { AutoFlush = true };
            System.Console.SetOut(stdout);
            var stderr = new StreamWriter(System.Console.OpenStandardError()) { AutoFlush = true };
            System.Console.SetError(stderr);
        }
    }
}
