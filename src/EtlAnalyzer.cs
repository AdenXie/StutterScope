using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace StutterScope
{
    public sealed class StallWindow
    {
        public double t, frameMs;
        public int pid;
        public string application, source;
    }
    public sealed class StallThread
    {
        public int tid;
        public string name;
        public double referenceCpuPct, runMs, readyMs, waitMs;
        public List<StallBlock> blocks = new List<StallBlock>();
    }
    // Blocks of one thread during the stall, grouped by where it waited and which process woke it.
    public sealed class StallBlock
    {
        public double ms, longestMs;
        public int count;
        public string waitStack, readiedBy, readyStack;
    }
    public sealed class StallModule
    {
        public string module;
        public double totalMs, maxMs, referenceMs; // referenceMs: the preceding second, scaled to the stall length.
        public int count;
    }
    public sealed class StallProcess
    {
        public string process, stack;
        public double stallCores, referenceCores;
    }
    public sealed class StallSummary
    {
        public double t, frameMs;
        public int pid;
        public string application, source;
        public bool covered;
        public List<StallThread> threads = new List<StallThread>();
        public List<StallProcess> cpu = new List<StallProcess>();
        public List<StallModule> dpc = new List<StallModule>();
        public int hardFaults, gameHardFaults;
        public double hardFaultMs, gameHardFaultMs;
        public List<string> conclusions = new List<string>();
    }
    public sealed class EtlSummary
    {
        public const int Schema = 2; // Bump when the summary logic changes so cached summaries are rebuilt.
        public int schema = Schema;
        public string file, error;
        public double firstT, lastT;
        public long events;
        public List<StallSummary> stalls = new List<StallSummary>();
    }

    // Reads a saved ETL through the Windows ETW consumer API (no WPA, symbols or elevation needed) and reduces the
    // scheduler, DPC/ISR and hard-fault events around a long frame to module-level evidence. Timestamps stay in raw
    // QPC units, the same clock as PresentMon and the session origin. Stacks are resolved to module names only.
    public sealed class EtlAnalyzer
    {
        delegate void EventCallback(IntPtr record);
        [DllImport("advapi32.dll", SetLastError = true)] static extern ulong OpenTraceW(IntPtr logfile);
        [DllImport("advapi32.dll", SetLastError = true)] static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll")] static extern uint CloseTrace(ulong handle);

        const int ThreadProvider = 0x3d6fa8d1, ProcessProvider = 0x3d6fa8d0, ImageProvider = 0x2cb15d1d;
        const int PerfInfoProvider = unchecked((int)0xce1dbfb4), PageFaultProvider = 0x3d6fa8d3, StackProvider = unchecked((int)0xdef2fe46);
        const double Lead = 1.6, Tail = 0.25; // Seconds kept before the stall start / after its end.

        struct Switch { public long ts; public int cpu, newTid, oldTid; public byte oldState; }
        struct Ready { public long ts; public int cpu, tid; }
        struct Routine { public long ts, initial; public ulong address; }
        struct Fault { public long ts, initial; public int tid; }
        sealed class Stack { public int tid; public ulong[] frames; }
        sealed class Image { public ulong start, end; public string name; }

        readonly long origin; readonly double frequency;
        readonly List<long[]> ranges = new List<long[]>();
        readonly List<Switch> switches = new List<Switch>();
        readonly List<Ready> readies = new List<Ready>();
        readonly List<Routine> routines = new List<Routine>();
        readonly List<Fault> faults = new List<Fault>();
        readonly Dictionary<long, List<Stack>> stacks = new Dictionary<long, List<Stack>>();
        readonly Dictionary<int, int> threadProcess = new Dictionary<int, int>();
        readonly Dictionary<int, string> threadNames = new Dictionary<int, string>(), processNames = new Dictionary<int, string>();
        readonly List<Image> kernelImages = new List<Image>();
        readonly Dictionary<int, List<Image>> userImages = new Dictionary<int, List<Image>>();
        long events, lastSwitch = Int64.MinValue;
        readonly long[] firstSwitch = new long[256]; // Per CPU: the circular buffers of idle and busy cores wrap at different times.

        EtlAnalyzer(long origin, double frequency) { this.origin = origin; this.frequency = frequency; }
        double Seconds(long ts) { return (ts - origin) / frequency; }
        long Raw(double seconds) { return origin + (long)(seconds * frequency); }
        bool Wanted(long ts)
        {
            for (int i = 0; i < ranges.Count; i++) if (ts >= ranges[i][0] && ts <= ranges[i][1]) return true;
            return false;
        }

        public static EtlSummary Analyze(string path, long origin, double frequency, IList<StallWindow> windows)
        {
            var summary = new EtlSummary { file = Path.GetFileName(path) };
            try
            {
                var analyzer = new EtlAnalyzer(origin, frequency);
                foreach (var w in windows) analyzer.ranges.Add(new[] { analyzer.Raw(w.t - w.frameMs / 1000 - Lead), analyzer.Raw(w.t + Tail) });
                analyzer.Read(path);
                summary.events = analyzer.events;
                if (analyzer.firstSwitch.Any(v => v != 0)) { summary.firstT = analyzer.Seconds(analyzer.firstSwitch.Where(v => v != 0).Max()); summary.lastT = analyzer.Seconds(analyzer.lastSwitch); }
                foreach (var w in windows) summary.stalls.Add(analyzer.Summarize(w, summary));
            }
            catch (Exception ex) { summary.error = ex.Message; }
            return summary;
        }

        void Read(string path)
        {
            // EVENT_TRACE_LOGFILEW is 448 bytes on x64; only the fields below are needed.
            IntPtr logfile = Marshal.AllocHGlobal(448), name = Marshal.StringToHGlobalUni(path);
            EventCallback callback = OnEvent;
            ulong handle = UInt64.MaxValue;
            try
            {
                for (int i = 0; i < 448; i += 8) Marshal.WriteInt64(logfile, i, 0);
                Marshal.WriteIntPtr(logfile, 0, name);
                Marshal.WriteInt32(logfile, 28, 0x10000000 | 0x00001000); // EVENT_RECORD callbacks with raw (QPC) timestamps.
                Marshal.WriteIntPtr(logfile, 424, Marshal.GetFunctionPointerForDelegate(callback));
                handle = OpenTraceW(logfile);
                if (handle == UInt64.MaxValue || handle == 0xFFFFFFFF) throw new IOException("无法打开ETL（错误 " + Marshal.GetLastWin32Error() + "）");
                long traceFrequency = Marshal.ReadInt64(logfile, 120 + 256); int clock = Marshal.ReadInt32(logfile, 120 + 272);
                if (clock != 1 || Math.Abs(traceFrequency - frequency) > 1) throw new InvalidDataException("ETL未使用与会话相同的QPC时钟，无法与逐帧数据对齐");
                uint rc = ProcessTrace(new[] { handle }, 1, IntPtr.Zero, IntPtr.Zero);
                if (rc != 0) throw new IOException("读取ETL失败（错误 " + rc + "）");
            }
            finally
            {
                if (handle != UInt64.MaxValue && handle != 0xFFFFFFFF) CloseTrace(handle);
                GC.KeepAlive(callback); Marshal.FreeHGlobal(name); Marshal.FreeHGlobal(logfile);
            }
        }

        static string Ansi(IntPtr data, int offset, int length)
        {
            var text = new StringBuilder();
            for (int i = offset; i < length; i++) { byte b = Marshal.ReadByte(data, i); if (b == 0) break; text.Append((char)b); }
            return text.ToString();
        }
        static string Wide(IntPtr data, int offset, int length)
        {
            var text = new StringBuilder();
            for (int i = offset; i + 1 < length; i += 2) { char c = (char)Marshal.ReadInt16(data, i); if (c == 0) break; text.Append(c); }
            return text.ToString();
        }

        void OnEvent(IntPtr record)
        {
            try
            {
                events++;
                long ts = Marshal.ReadInt64(record, 16);
                int provider = Marshal.ReadInt32(record, 24), opcode = Marshal.ReadByte(record, 45), version = Marshal.ReadByte(record, 42);
                int length = (ushort)Marshal.ReadInt16(record, 86); IntPtr data = Marshal.ReadIntPtr(record, 96);
                if (data == IntPtr.Zero) return;
                if (provider == ThreadProvider)
                {
                    if (opcode == 36 && length >= 24)
                    {
                        int cpu = Marshal.ReadByte(record, 80); if (firstSwitch[cpu] == 0) firstSwitch[cpu] = ts; if (ts > lastSwitch) lastSwitch = ts;
                        if (Wanted(ts)) switches.Add(new Switch { ts = ts, cpu = Marshal.ReadByte(record, 80), newTid = Marshal.ReadInt32(data, 0),
                            oldTid = Marshal.ReadInt32(data, 4), oldState = Marshal.ReadByte(data, 14) });
                    }
                    else if (opcode == 50 && length >= 4) { if (Wanted(ts)) readies.Add(new Ready { ts = ts, cpu = Marshal.ReadByte(record, 80), tid = Marshal.ReadInt32(data, 0) }); }
                    else if (opcode >= 1 && opcode <= 4 && length >= 8) threadProcess[Marshal.ReadInt32(data, 4)] = Marshal.ReadInt32(data, 0);
                    else if (opcode == 72 && length > 8) threadNames[Marshal.ReadInt32(data, 4)] = Wide(data, 8, length);
                }
                else if (provider == PerfInfoProvider)
                {
                    // DPC, timer DPC, threaded DPC and both ISR forms begin with {InitialTime, Routine}.
                    if ((opcode == 66 || opcode == 67 || opcode == 68 || opcode == 69 || opcode == 50) && length >= 16 && Wanted(ts))
                        routines.Add(new Routine { ts = ts, initial = Marshal.ReadInt64(data, 0), address = (ulong)Marshal.ReadInt64(data, 8) });
                }
                else if (provider == StackProvider)
                {
                    if (opcode != 32 || length < 24) return;
                    long eventTime = Marshal.ReadInt64(data, 0);
                    if (!Wanted(eventTime)) return;
                    int count = Math.Min(64, (length - 16) / 8); var frames = new ulong[count];
                    for (int i = 0; i < count; i++) frames[i] = (ulong)Marshal.ReadInt64(data, 16 + i * 8);
                    List<Stack> list; if (!stacks.TryGetValue(eventTime, out list)) stacks[eventTime] = list = new List<Stack>(2);
                    list.Add(new Stack { tid = Marshal.ReadInt32(data, 12), frames = frames });
                }
                else if (provider == PageFaultProvider)
                {
                    if (opcode == 32 && length >= 36 && Wanted(ts)) faults.Add(new Fault { ts = ts, initial = Marshal.ReadInt64(data, 0), tid = Marshal.ReadInt32(data, 32) });
                }
                else if (provider == ProcessProvider)
                {
                    if (!(opcode >= 1 && opcode <= 4) && opcode != 39) return;
                    int offset = version >= 4 ? 36 : 32;
                    if (length < offset + 8) return;
                    int pid = Marshal.ReadInt32(data, 8);
                    if (Marshal.ReadInt32(data, offset) == 0) offset += 4;
                    else { offset += 16; if (offset + 2 > length) return; offset += 8 + 4 * Marshal.ReadByte(data, offset + 1); }
                    if (offset < length) { string name = Ansi(data, offset, length); if (name.Length > 0) processNames[pid] = name; }
                }
                else if (provider == ImageProvider)
                {
                    if ((opcode != 10 && opcode != 3 && opcode != 4) || length < 58) return;
                    ulong start = (ulong)Marshal.ReadInt64(data, 0), size = (ulong)Marshal.ReadInt64(data, 8); int pid = Marshal.ReadInt32(data, 16);
                    string file = Wide(data, 56, length); int slash = file.LastIndexOf('\\');
                    var image = new Image { start = start, end = start + size, name = slash >= 0 ? file.Substring(slash + 1) : file };
                    if (start >= 0xFFFF000000000000UL) kernelImages.Add(image);
                    else { List<Image> list; if (!userImages.TryGetValue(pid, out list)) userImages[pid] = list = new List<Image>(); list.Add(image); }
                }
            }
            catch (AccessViolationException) { }
            catch (ArgumentException) { }
        }

        string Module(int pid, ulong address)
        {
            List<Image> list = kernelImages;
            if (address < 0xFFFF000000000000UL && !userImages.TryGetValue(pid, out list)) return "?";
            for (int i = list.Count - 1; i >= 0; i--) if (address >= list[i].start && address < list[i].end) return list[i].name;
            return "?";
        }
        string Process(int tid)
        {
            int pid; if (!threadProcess.TryGetValue(tid, out pid)) return "未知线程 " + tid;
            string name; return (pid == 0 ? "Idle" : processNames.TryGetValue(pid, out name) ? name : "PID") + " (" + pid + ")";
        }
        string Chain(long ts, int tid)
        {
            List<Stack> list; if (!stacks.TryGetValue(ts, out list)) return null;
            int pid; threadProcess.TryGetValue(tid, out pid);
            var modules = new List<string>();
            foreach (var stack in list)
            {
                if (stack.tid != tid) continue;
                foreach (ulong address in stack.frames)
                {
                    string module = Module(pid, address);
                    if (modules.Count == 0 || modules[modules.Count - 1] != module) modules.Add(module);
                }
            }
            return modules.Count == 0 ? null : String.Join(" ← ", modules.Take(9));
        }
        static double Overlap(double a, double b, double lo, double hi) { return Math.Max(0, Math.Min(b, hi) - Math.Max(a, lo)); }

        StallSummary Summarize(StallWindow w, EtlSummary file)
        {
            var result = new StallSummary { t = w.t, frameMs = w.frameMs, pid = w.pid, application = w.application, source = w.source };
            double stallStart = w.t - w.frameMs / 1000, stallEnd = w.t, refStart = stallStart - 1.05, refEnd = stallStart - 0.05, span = stallEnd - stallStart;
            long lo = Raw(stallStart - Lead), hi = Raw(stallEnd + Tail);
            var cs = switches.Where(s => s.ts >= lo && s.ts <= hi).OrderBy(s => s.ts).ToList();
            result.covered = cs.Count > 100 && file.firstT <= refStart && file.lastT >= stallEnd;
            if (!result.covered) { result.conclusions.Add("这份ETL的循环缓冲没有完整覆盖该卡顿前后（线程切换事件的完整保留范围约为 " + file.firstT.ToString("F1", CultureInfo.InvariantCulture) + "–" + file.lastT.ToString("F1", CultureInfo.InvariantCulture) + " 秒）。"); return result; }

            // 1. Who was on the CPUs, stall vs the preceding second.
            var onCpu = new Dictionary<int, Switch>();
            var threadStall = new Dictionary<int, double>(); var threadRef = new Dictionary<int, double>();
            foreach (var s in cs)
            {
                Switch previous;
                if (onCpu.TryGetValue(s.cpu, out previous))
                {
                    double a = Seconds(previous.ts), b = Seconds(s.ts), v;
                    double stall = Overlap(a, b, stallStart, stallEnd), reference = Overlap(a, b, refStart, refEnd);
                    if (stall > 0) { threadStall.TryGetValue(previous.newTid, out v); threadStall[previous.newTid] = v + stall; }
                    if (reference > 0) { threadRef.TryGetValue(previous.newTid, out v); threadRef[previous.newTid] = v + reference; }
                }
                onCpu[s.cpu] = s;
            }
            var processStall = new Dictionary<string, double>(); var processRef = new Dictionary<string, double>();
            foreach (var kv in threadStall) { double v; string p = Process(kv.Key); processStall.TryGetValue(p, out v); processStall[p] = v + kv.Value; }
            foreach (var kv in threadRef) { double v; string p = Process(kv.Key); processRef.TryGetValue(p, out v); processRef[p] = v + kv.Value; }
            foreach (string p in processStall.Keys.Union(processRef.Keys).Where(p => !p.StartsWith("Idle ", StringComparison.Ordinal)))
            {
                double a, b; processStall.TryGetValue(p, out a); processRef.TryGetValue(p, out b);
                result.cpu.Add(new StallProcess { process = p, stallCores = a / span, referenceCores = b / (refEnd - refStart) });
            }
            result.cpu = result.cpu.OrderByDescending(p => Math.Max(p.stallCores, p.referenceCores)).Take(8).ToList();
            // For processes that became busier during the stall, show where their busiest thread was being scheduled from.
            foreach (var p in result.cpu.Where(p => p.stallCores >= .05 && p.stallCores >= p.referenceCores * 1.4))
            {
                var busy = threadStall.Where(kv => Process(kv.Key) == p.process).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
                p.stack = cs.Where(s => s.newTid == busy && Seconds(s.ts) >= stallStart && Seconds(s.ts) <= stallEnd).Select(s => Chain(s.ts, busy))
                    .Where(c => c != null).GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            }

            // 2. The game's busiest threads: running, ready (wants a CPU) or blocked during the stall, and what ended the block.
            Func<int, bool> inGame = tid => { int pid; return threadProcess.TryGetValue(tid, out pid) && pid == w.pid; };
            var busiest = threadRef.Where(kv => inGame(kv.Key)).OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key).ToList();
            if (busiest.Count == 0) busiest = threadStall.Where(kv => inGame(kv.Key)).OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key).ToList();
            var rd = readies.Where(r => r.ts >= lo && r.ts <= hi).OrderBy(r => r.ts).ToList();
            foreach (int tid in busiest)
            {
                double reference; threadRef.TryGetValue(tid, out reference); string name; threadNames.TryGetValue(tid, out name);
                var thread = new StallThread { tid = tid, name = name, referenceCpuPct = reference / (refEnd - refStart) * 100 };
                // state: 0 unknown, 1 running, 2 ready, 3 waiting
                int state = 0, i = 0, j = 0; double since = stallStart - Lead, blockStart = -1; long readyTs = 0; int readyCpu = -1, readier = 0;
                var current = new Dictionary<int, int>(); var groups = new Dictionary<string, StallBlock>();
                while (i < cs.Count || j < rd.Count)
                {
                    bool takeSwitch = j >= rd.Count || (i < cs.Count && cs[i].ts <= rd[j].ts);
                    if (takeSwitch)
                    {
                        var s = cs[i++]; double now = Seconds(s.ts);
                        if (s.oldTid == tid)
                        {
                            thread.runMs += Overlap(since, now, stallStart, stallEnd) * 1000;
                            bool waiting = s.oldState == 5 || s.oldState == 6 || s.oldState == 4;
                            state = waiting ? 3 : 2; since = now; blockStart = now; readyTs = 0; readier = 0;
                        }
                        if (s.newTid == tid)
                        {
                            if (state == 2) thread.readyMs += Overlap(since, now, stallStart, stallEnd) * 1000;
                            else if (state == 3) thread.waitMs += Overlap(since, now, stallStart, stallEnd) * 1000;
                            if (blockStart >= 0)
                            {
                                double blocked = Overlap(blockStart, now, stallStart, stallEnd) * 1000;
                                if (blocked > 0)
                                {
                                    string stack = Chain(s.ts, tid), by = readyTs == 0 ? "被抢占后重新调度" : readier == 0 ? "中断/DPC 或未知线程" : Process(readier);
                                    string key = by + "|" + stack; StallBlock block;
                                    if (!groups.TryGetValue(key, out block))
                                        groups[key] = block = new StallBlock { waitStack = stack, readiedBy = by, readyStack = readyTs == 0 || readier == 0 ? null : Chain(readyTs, readier) };
                                    block.ms += blocked; block.count++; block.longestMs = Math.Max(block.longestMs, blocked);
                                }
                            }
                            state = 1; since = now; blockStart = -1;
                        }
                        current[s.cpu] = s.newTid;
                    }
                    else
                    {
                        var r = rd[j++];
                        if (r.tid != tid || state != 3) continue;
                        double now = Seconds(r.ts);
                        thread.waitMs += Overlap(since, now, stallStart, stallEnd) * 1000; state = 2; since = now;
                        readyTs = r.ts; readyCpu = r.cpu; current.TryGetValue(readyCpu, out readier);
                    }
                }
                double end = stallEnd + Tail;
                if (state == 1) thread.runMs += Overlap(since, end, stallStart, stallEnd) * 1000;
                else if (state == 2) thread.readyMs += Overlap(since, end, stallStart, stallEnd) * 1000;
                else if (state == 3) thread.waitMs += Overlap(since, end, stallStart, stallEnd) * 1000;
                thread.blocks = groups.Values.OrderByDescending(b => b.ms).Take(8).ToList();
                result.threads.Add(thread);
            }

            // 3. DPC / ISR by driver module, 4. hard faults.
            long stallLo = Raw(stallStart), stallHi = Raw(stallEnd);
            result.dpc = routines.Where(r => r.ts >= stallLo && r.ts <= stallHi && r.ts >= r.initial).GroupBy(r => Module(0, r.address))
                .Select(g => new StallModule { module = g.Key, count = g.Count(), totalMs = g.Sum(r => (r.ts - r.initial) / frequency * 1000), maxMs = g.Max(r => (r.ts - r.initial) / frequency * 1000) })
                .OrderByDescending(m => m.totalMs).Take(6).ToList();
            long refLo = Raw(refStart), refHi = Raw(refEnd);
            foreach (var module in result.dpc)
                module.referenceMs = routines.Where(r => r.ts >= refLo && r.ts <= refHi && r.ts >= r.initial && Module(0, r.address) == module.module)
                    .Sum(r => (r.ts - r.initial) / frequency * 1000) * span / (refEnd - refStart);
            foreach (var f in faults.Where(f => f.ts >= stallLo && f.ts <= stallHi && f.ts >= f.initial))
            {
                double ms = (f.ts - f.initial) / frequency * 1000; result.hardFaults++; result.hardFaultMs += ms;
                if (inGame(f.tid)) { result.gameHardFaults++; result.gameHardFaultMs += ms; }
            }
            Conclude(result, span * 1000);
            return result;
        }

        static string F(double value) { return value.ToString("F0", CultureInfo.InvariantCulture); }
        // The recorder's own processes are busy right after a trigger; they are not suspects.
        static bool Own(string process) { return process.StartsWith("StutterScope", StringComparison.OrdinalIgnoreCase) || process.StartsWith("PresentMon", StringComparison.OrdinalIgnoreCase) || process.StartsWith("wpr.exe", StringComparison.OrdinalIgnoreCase); }
        static void Conclude(StallSummary s, double spanMs)
        {
            var lines = s.conclusions;
            var main = s.threads.FirstOrDefault();
            if (main == null) { lines.Add("ETL中没有找到该进程的线程调度记录（进程可能受保护，或线程信息未进入缓冲）。"); return; }
            string label = "游戏最忙线程 " + main.tid + (String.IsNullOrEmpty(main.name) ? "" : "（" + main.name + "）");
            lines.Add(label + " 在这 " + F(spanMs) + " ms 内：运行 " + F(main.runMs) + " ms，就绪等CPU " + F(main.readyMs) + " ms，阻塞等待 " + F(main.waitMs) + " ms。");
            var top = main.blocks.FirstOrDefault();
            if (main.waitMs + main.readyMs >= spanMs * .4 && top != null)
            {
                lines.Add("主要是阻塞等待而不是计算。其中 " + F(top.ms) + " ms（" + top.count + " 次，单次最长 " + F(top.longestMs) + " ms）"
                    + (top.waitStack == null ? "" : "等待在模块链（近→远）：" + top.waitStack) + "；唤醒方：" + top.readiedBy
                    + (top.readyStack == null ? "" : "（" + top.readyStack + "）") + "。唤醒方通常就是它在等的那个进程或驱动。");
            }
            double windowing = main.blocks.Where(b => b.waitStack != null && b.waitStack.IndexOf("win32k", StringComparison.OrdinalIgnoreCase) >= 0 && b.readiedBy.IndexOf("(", StringComparison.Ordinal) >= 0).Sum(b => b.ms);
            if (windowing >= spanMs * .25)
                lines.Add("至少 " + F(windowing) + " ms 花在窗口子系统（win32k）调用里，逐个等待其他进程应答：" + String.Join("、", main.blocks.Where(b => b.waitStack != null && b.waitStack.IndexOf("win32k", StringComparison.OrdinalIgnoreCase) >= 0 && b.readiedBy.IndexOf("(", StringComparison.Ordinal) >= 0).Select(b => b.readiedBy).Distinct().Take(6))
                    + "。这是游戏线程在向其他程序的窗口发同步消息/枚举窗口的特征（常见于反作弊或覆盖层的定时扫描）；开着的带窗口后台程序越多、越慢，这一下就越长。");
            if (main.waitMs + main.readyMs < spanMs * .4 && main.runMs >= spanMs * .6)
                lines.Add("该线程几乎一直在运行：这是游戏自身的长计算（资源加载、着色器/脚本、GC 等），不是被系统或驱动挂起。CPU 单核速度直接决定这类卡顿的长短。");
            if (main.readyMs >= Math.Max(8, spanMs * .2))
                lines.Add("线程已就绪却等了 " + F(main.readyMs) + " ms 才拿到CPU：存在抢占或调度延迟，请对照下方同期占用CPU的进程。");
            var worst = s.dpc.OrderByDescending(d => d.maxMs).FirstOrDefault();
            if (worst != null && worst.maxMs >= 1) lines.Add("出现较长的单次 DPC/ISR：" + worst.module + " 最长 " + worst.maxMs.ToString("F2", CultureInfo.InvariantCulture) + " ms。");
            double dpcTotal = s.dpc.Sum(d => d.totalMs);
            if (dpcTotal >= spanMs * .1 && s.dpc.Count > 0 && s.dpc[0].totalMs >= s.dpc[0].referenceMs * 2)
                lines.Add("DPC/ISR 合计 " + F(dpcTotal) + " ms，明显高于卡顿前；最多的是 " + s.dpc[0].module + "（" + F(s.dpc[0].totalMs) + " ms / " + s.dpc[0].count + " 次，卡顿前同样时长约 " + F(s.dpc[0].referenceMs) + " ms）。");
            if (s.gameHardFaultMs >= 5) lines.Add("游戏线程发生 " + s.gameHardFaults + " 次硬缺页，合计等待磁盘 " + F(s.gameHardFaultMs) + " ms（内存紧张或首次读取资源）。");
            var intruder = s.cpu.Where(p => p.process.IndexOf("(" + s.pid + ")", StringComparison.Ordinal) < 0 && !Own(p.process) && p.stallCores >= .5 && p.stallCores >= p.referenceCores * 2).OrderByDescending(p => p.stallCores).FirstOrDefault();
            if (intruder != null) lines.Add("同期 " + intruder.process + " 的CPU占用升到 " + intruder.stallCores.ToString("F1", CultureInfo.InvariantCulture) + " 个核心（此前 " + intruder.referenceCores.ToString("F1", CultureInfo.InvariantCulture) + "）。");
            foreach (var p in s.cpu.Where(p => p.stack != null && p.process.IndexOf("(" + s.pid + ")", StringComparison.Ordinal) < 0 && !Own(p.process)).Take(3))
                lines.Add("卡顿期间变活跃的进程 " + p.process + "（" + p.referenceCores.ToString("F2", CultureInfo.InvariantCulture) + " → " + p.stallCores.ToString("F2", CultureInfo.InvariantCulture) + " 个核心），其最忙线程的调用位置：" + p.stack + "。");
        }
    }
}
