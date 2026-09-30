// NGenuityLeakFix - closes the process handles that HyperX NGENUITY's
// NGenuity2Helper.exe opens and never closes.
//
// Written against C# 5 on purpose: it has to compile with the csc.exe that
// ships with Windows (.NET Framework 4.x) and with Windows PowerShell 5.1's
// Add-Type, so nobody needs an SDK to build or audit it.
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
//
// It never touches any other process and never touches handles of any other
// type. It needs no administrator rights: the helper runs as the logged-on
// user, and that user may duplicate handles out of their own processes.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

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
        public string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NGenuityLeakFix", "NGenuityLeakFix.log");

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
                    default: throw new ArgumentException("Unknown argument: " + args[i]);
                }
            }
            return o;
        }
    }

    public sealed class TickResult
    {
        public int HelperPid;
        public int ProcessHandles;
        public int TotalHandles;
        public int Closed;
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
        IntPtr helper = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        int bufferSize = 16 * 1024 * 1024;

        public Fixer(Options options)
        {
            opt = options;
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
                r.Note = "below threshold (" + opt.Threshold + "), nothing to do";
                return r;
            }

            DateTime cutoff = now.AddSeconds(-opt.MinAgeSeconds);

            // Exit status can change after a handle was first seen: refresh it
            // for every handle that is old enough to be a candidate.
            foreach (var kv in entries)
                if (!kv.Value.Exited && kv.Value.FirstSeen <= cutoff) Inspect(kv.Key, kv.Value);

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
                if (opt.DryRun) { r.Closed++; continue; }
                IntPtr ignored;
                if (Native.DuplicateHandle(helper, new IntPtr(h), IntPtr.Zero, out ignored, 0, false, Native.DUPLICATE_CLOSE_SOURCE))
                {
                    r.Closed++;
                    entries.Remove(h);
                }
                else r.Failed++;
            }
            if (opt.DryRun) r.Note = "dry run, nothing was closed";
            return r;
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
            if (helper != IntPtr.Zero) { Native.CloseHandle(helper); helper = IntPtr.Zero; }
        }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Options opt;
            try { opt = Options.Parse(args); }
            catch (Exception ex) { ShowError(ex.Message + "\n\n" + Usage); return 2; }
            return Run(opt);
        }

        public const string Usage =
            "NGenuityLeakFix [--interval 5] [--min-age 30] [--threshold 500] [--dry-run] [--once] [--console] [--verbose] [--log path]";

        public static int Run(Options opt)
        {
            if (opt.Console) Native.AttachOrAllocConsole();
            if (IntPtr.Size != 8) { Log(opt, "This tool only supports 64-bit Windows."); return 1; }

            bool created;
            using (var mutex = new Mutex(true, @"Local\NGenuityLeakFix", out created))
            {
                // Two instances closing handles in the same helper could race
                // on a reused handle value, so only a dry run may run alongside
                // an installed instance.
                if (!created && !(opt.Once && opt.DryRun)) { Log(opt, "Another instance is already running, exiting."); return 0; }

                Fixer fixer;
                try { fixer = new Fixer(opt); }
                catch (Exception ex) { Log(opt, "Cannot start: " + ex.Message); return 1; }
                Log(opt, "Started (interval " + opt.IntervalSeconds + "s, min-age " + opt.MinAgeSeconds +
                         "s, threshold " + opt.Threshold + (opt.DryRun ? ", DRY RUN" : "") + ").");

                long closedSinceReport = 0;
                DateTime lastReport = DateTime.UtcNow;
                string lastNote = null;
                while (true)
                {
                    TickResult r;
                    try { r = fixer.Tick(); }
                    catch (Exception ex) { Log(opt, "Error: " + ex.Message); r = null; }

                    if (r != null)
                    {
                        if (r.Closed < 1000) closedSinceReport += r.Closed;
                        if (opt.Verbose || opt.Once || r.Closed >= 1000)
                            Log(opt, Describe(r));
                        else if (r.Note != lastNote && r.Closed == 0)
                            Log(opt, Describe(r));
                        else if ((DateTime.UtcNow - lastReport).TotalSeconds >= 600 && closedSinceReport > 0)
                        {
                            Log(opt, "pid " + r.HelperPid + ": " + r.ProcessHandles + " process handles, closed " +
                                     closedSinceReport + " in the last 10 minutes");
                            closedSinceReport = 0;
                            lastReport = DateTime.UtcNow;
                        }
                        lastNote = r.Note;
                    }

                    if (opt.Once) return 0;
                    Thread.Sleep(opt.IntervalSeconds * 1000);
                }
            }
        }

        static string Describe(TickResult r)
        {
            if (r.HelperPid == 0) return r.Note;
            var s = "pid " + r.HelperPid + ": " + r.TotalHandles + " handles, " + r.ProcessHandles + " of type Process";
            if (r.Closed > 0 || r.Failed > 0)
                s += "; closed " + r.Closed + " (" + r.ClosedDuplicate + " duplicate, " + r.ClosedExited + " to exited processes)" +
                     (r.Failed > 0 ? ", failed " + r.Failed : "");
            if (r.Note != null) s += "; " + r.Note;
            return s;
        }

        static readonly object logLock = new object();

        static void Log(Options opt, string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message;
            if (opt.Console) System.Console.WriteLine(line);
            lock (logLock)
            {
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

        static void ShowError(string message)
        {
            Native.AttachOrAllocConsole();
            System.Console.Error.WriteLine(message);
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

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

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
