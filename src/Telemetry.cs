using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace StutterScope
{
    // All timestamps use the same Windows QPC clock as PresentMon.
    public static class Clock
    {
        public static double Seconds(long origin) { return (Stopwatch.GetTimestamp() - origin) / (double)Stopwatch.Frequency; }
    }

    public sealed class Pdh : IDisposable
    {
        IntPtr query;
        readonly Dictionary<string, IntPtr> counters = new Dictionary<string, IntPtr>();
        public readonly List<string> Errors = new List<string>();
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        struct Value { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Number; }
        [StructLayout(LayoutKind.Sequential)]
        struct Item { public IntPtr Name; public Value Value; }
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string source, IntPtr data, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr data, out IntPtr counter);
        [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out Value value);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
        [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);

        public Pdh()
        {
            uint rc = PdhOpenQueryW(null, IntPtr.Zero, out query);
            if (rc != 0) throw new InvalidOperationException("PDH query: 0x" + rc.ToString("X"));
        }
        public void Add(string key, string path)
        {
            IntPtr counter;
            uint rc = PdhAddEnglishCounterW(query, path, IntPtr.Zero, out counter);
            if (rc == 0) counters[key] = counter;
            else Errors.Add(path + " (0x" + rc.ToString("X") + ")");
        }
        public void Collect()
        {
            uint rc = PdhCollectQueryData(query);
            if (rc != 0) throw new InvalidOperationException("PDH collect: 0x" + rc.ToString("X"));
        }
        public double? Read(string key)
        {
            IntPtr counter; uint type; Value value;
            if (!counters.TryGetValue(key, out counter)) return null;
            if (PdhGetFormattedCounterValue(counter, 0x200 | 0x8000, out type, out value) != 0 || value.Status > 1 || !Finite(value.Number)) return null;
            return value.Number;
        }
        public Dictionary<string, double> Array(string key)
        {
            var result = new Dictionary<string, double>();
            IntPtr counter;
            if (!counters.TryGetValue(key, out counter)) return result;
            // Instances (including newly launched games) are expanded by PDH on every collection.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint bytes = 0, count;
                uint rc = PdhGetFormattedCounterArrayW(counter, 0x200 | 0x8000, ref bytes, out count, IntPtr.Zero);
                if (rc != 0x800007D2 || bytes == 0) return result;
                IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
                try
                {
                    rc = PdhGetFormattedCounterArrayW(counter, 0x200 | 0x8000, ref bytes, out count, buffer);
                    if (rc == 0x800007D2) continue;
                    if (rc != 0) return result;
                    int stride = Marshal.SizeOf(typeof(Item));
                    for (int i = 0; i < count; i++)
                    {
                        var item = (Item)Marshal.PtrToStructure(IntPtr.Add(buffer, i * stride), typeof(Item));
                        if (item.Value.Status <= 1 && Finite(item.Value.Number)) result[Marshal.PtrToStringUni(item.Name)] = item.Value.Number;
                    }
                    return result;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return result;
        }
        public static bool Finite(double n) { return !Double.IsNaN(n) && !Double.IsInfinity(n); }
        public void Dispose() { if (query != IntPtr.Zero) { PdhCloseQuery(query); query = IntPtr.Zero; } }
    }

    public sealed class ProcessLoad
    {
        public int pid;
        public string name;
        public double cpu;
        public double? readMB, writeMB;
        public double memoryMB;
    }
    public sealed class SlowSample
    {
        public double t;
        public double? gpu;
        public Dictionary<string, double> gpuEngines, dedicatedMB, sharedMB;
        public List<ProcessLoad> processes = new List<ProcessLoad>();
        public int readableProcesses, inaccessibleProcesses;
        public double costMs;
    }
    public sealed class Sample
    {
        public double t, intervalMs, costMs, probeLateMs;
        public double? cpu, coreMax, dpc, dpcMax, interrupt, interruptMax, cpuMHz, cpuPerformance;
        public double? availableMB, commitPct, pagesIn, pagesOut, diskReadMB, diskWriteMB, diskReadMs, diskWriteMs, diskQueue;
        public string diskWorst;
        public int foregroundPid, acPower;
        public string foreground;
        public double? monitorCpu;
        public double monitorMemoryMB;
    }

    public sealed class FastTelemetry : IDisposable
    {
        readonly Pdh pdh = new Pdh();
        int lastForegroundPid = -1;
        string lastForegroundName = "";
        [StructLayout(LayoutKind.Sequential)] struct PowerStatus { public byte AC, Flag, Percent, Reserved; public uint Life, FullLife; }
        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus status);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        public List<string> Errors { get { return pdh.Errors; } }

        public FastTelemetry()
        {
            pdh.Add("cpu", @"\Processor(*)\% Processor Time");
            pdh.Add("dpc", @"\Processor(*)\% DPC Time");
            pdh.Add("isr", @"\Processor(*)\% Interrupt Time");
            pdh.Add("mhz", @"\Processor Information(_Total)\Processor Frequency");
            pdh.Add("perf", @"\Processor Information(_Total)\% Processor Performance");
            pdh.Add("memory", @"\Memory\Available MBytes");
            pdh.Add("commit", @"\Memory\% Committed Bytes In Use");
            pdh.Add("in", @"\Memory\Pages Input/sec");
            pdh.Add("out", @"\Memory\Pages Output/sec");
            pdh.Add("read", @"\PhysicalDisk(*)\Disk Read Bytes/sec");
            pdh.Add("write", @"\PhysicalDisk(*)\Disk Write Bytes/sec");
            pdh.Add("readMs", @"\PhysicalDisk(*)\Avg. Disk sec/Read");
            pdh.Add("writeMs", @"\PhysicalDisk(*)\Avg. Disk sec/Write");
            pdh.Add("queue", @"\PhysicalDisk(*)\Current Disk Queue Length");
            pdh.Collect();
        }
        static double? Total(Dictionary<string, double> values)
        {
            double v; return values.TryGetValue("_Total", out v) ? (double?)v : null;
        }
        static double? Maximum(Dictionary<string, double> values)
        {
            var cores = values.Where(p => p.Key != "_Total").Select(p => p.Value).ToList();
            return cores.Count == 0 ? null : (double?)cores.Max();
        }
        public Sample Read()
        {
            pdh.Collect();
            var cpu = pdh.Array("cpu"); var dpc = pdh.Array("dpc"); var isr = pdh.Array("isr");
            var reads = pdh.Array("readMs"); var writes = pdh.Array("writeMs");
            var worst = reads.Concat(writes).Where(p => p.Key != "_Total").OrderByDescending(p => p.Value).FirstOrDefault();
            uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            if ((int)pid != lastForegroundPid)
            {
                lastForegroundPid = (int)pid;
                try { using (var p = Process.GetProcessById((int)pid)) lastForegroundName = p.ProcessName; }
                catch { lastForegroundName = "unknown"; }
            }
            PowerStatus power; int ac = GetSystemPowerStatus(out power) ? power.AC : 255;
            return new Sample {
                cpu = Total(cpu), coreMax = Maximum(cpu), dpc = Total(dpc), dpcMax = Maximum(dpc), interrupt = Total(isr), interruptMax = Maximum(isr),
                cpuMHz = pdh.Read("mhz"), cpuPerformance = pdh.Read("perf"), availableMB = pdh.Read("memory"), commitPct = pdh.Read("commit"),
                pagesIn = pdh.Read("in"), pagesOut = pdh.Read("out"), diskReadMB = Total(pdh.Array("read")) / 1048576,
                diskWriteMB = Total(pdh.Array("write")) / 1048576, diskReadMs = Maximum(reads) * 1000, diskWriteMs = Maximum(writes) * 1000,
                diskQueue = Maximum(pdh.Array("queue")), diskWorst = worst.Key, foregroundPid = (int)pid, foreground = lastForegroundName, acPower = ac
            };
        }
        public void Dispose() { pdh.Dispose(); }
    }

    public sealed class SlowTelemetry : IDisposable
    {
        readonly Pdh pdh = new Pdh();
        readonly Dictionary<int, Previous> previous = new Dictionary<int, Previous>();
        struct Previous { public double cpu, t; public ulong read, write; public bool io; public long created; }
        [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        public List<string> Errors { get { return pdh.Errors; } }
        public SlowTelemetry()
        {
            pdh.Add("gpu", @"\GPU Engine(*)\Utilization Percentage");
            pdh.Add("dedicated", @"\GPU Adapter Memory(*)\Dedicated Usage");
            pdh.Add("shared", @"\GPU Adapter Memory(*)\Shared Usage");
            pdh.Collect();
        }
        public SlowSample Read(double t)
        {
            pdh.Collect();
            var engines = new Dictionary<string, double>();
            foreach (var kv in pdh.Array("gpu"))
            {
                // Sum processes sharing the SAME physical engine; never sum unrelated engines.
                int start = kv.Key.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
                string key = start >= 0 ? kv.Key.Substring(start) : kv.Key;
                double old; engines.TryGetValue(key, out old); engines[key] = old + kv.Value;
            }
            foreach (string key in engines.Keys.ToList()) engines[key] = Math.Min(100, engines[key]);
            var sample = new SlowSample { t = t, gpuEngines = engines,
                gpu = engines.Count > 0 ? (double?)engines.Values.Max() : null,
                dedicatedMB = pdh.Array("dedicated").ToDictionary(p => p.Key, p => p.Value / 1048576),
                sharedMB = pdh.Array("shared").ToDictionary(p => p.Key, p => p.Value / 1048576) };
            var loads = new List<ProcessLoad>();
            var next = new Dictionary<int, Previous>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        int pid = process.Id;
                        if (pid == 0) continue;
                        double cpu = process.TotalProcessorTime.TotalSeconds;
                        long created = process.StartTime.ToUniversalTime().Ticks;
                        IoCounters io = new IoCounters();
                        IntPtr handle = OpenProcess(0x1000, false, pid);
                        bool gotIo = false;
                        try { if (handle != IntPtr.Zero) gotIo = GetProcessIoCounters(handle, out io); }
                        finally { if (handle != IntPtr.Zero) CloseHandle(handle); }
                        var current = new Previous { cpu = cpu, t = t, read = io.ReadBytes, write = io.WriteBytes, io = gotIo, created = created };
                        next[pid] = current; sample.readableProcesses++;
                        Previous old;
                        if (!previous.TryGetValue(pid, out old) || t <= old.t || old.created != created) continue;
                        double dt = t - old.t;
                        loads.Add(new ProcessLoad { pid = pid, name = process.ProcessName,
                            cpu = Math.Max(0, (cpu - old.cpu) / dt / Environment.ProcessorCount * 100), memoryMB = process.WorkingSet64 / 1048576.0,
                            readMB = gotIo && old.io && io.ReadBytes >= old.read ? (double?)((io.ReadBytes - old.read) / dt / 1048576) : null,
                            writeMB = gotIo && old.io && io.WriteBytes >= old.write ? (double?)((io.WriteBytes - old.write) / dt / 1048576) : null });
                    }
                    catch (System.ComponentModel.Win32Exception) { sample.inaccessibleProcesses++; }
                    catch (InvalidOperationException) { sample.inaccessibleProcesses++; }
                }
            }
            previous.Clear(); foreach (var kv in next) previous[kv.Key] = kv.Value;
            sample.processes = loads.OrderByDescending(p => p.cpu).Take(6)
                .Union(loads.OrderByDescending(p => (p.readMB ?? 0) + (p.writeMB ?? 0)).Take(6)).ToList();
            return sample;
        }
        public void Dispose() { pdh.Dispose(); }
    }
}
