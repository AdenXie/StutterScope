using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace StutterScope
{
    public static class Json
    {
        public static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 100 }; }
        public static string Encode(object value) { return Serializer().Serialize(value); }
        public static T Decode<T>(string value) { return Serializer().Deserialize<T>(value); }
        public static void Save(string path, object value) { File.WriteAllText(path, Encode(value), new UTF8Encoding(false)); }
        public static StreamWriter Writer(string path) { return new StreamWriter(path, false, new UTF8Encoding(false), 65536); }
    }

    public sealed class Session
    {
        public readonly string Folder;
        public readonly long Origin = Stopwatch.GetTimestamp();
        public readonly DateTime StartedUtc = DateTime.UtcNow;
        public readonly string TraceName = "StutterScope_" + Guid.NewGuid().ToString("N");
        public readonly Dictionary<string, object> Meta = new Dictionary<string, object>();
        public readonly string ProcessFilter;
        public volatile string StopReason;
        public long FrameRows;
        public volatile string FrameStatus = "尚未启动";
        public volatile Sample Latest;
        public volatile SlowSample LatestSlow;
        public DeepTrace Deep { get; private set; }
        long foregroundSinceQpc;
        readonly ManualResetEvent stopped = new ManualResetEvent(false);
        readonly object logLock = new object();
        readonly HashSet<string> warnings = new HashSet<string>();
        readonly ConcurrentQueue<double[]> probeGaps = new ConcurrentQueue<double[]>();
        long maxProbeUs;
        Thread fastThread, slowThread, probeThread, frameThread, stderrThread;
        int frameAttempts;
        Process presentmon;
        string helper { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "PresentMon-2.6.0-x64.exe"); } }
        public static bool IsAdmin { get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } }

        public Session(string root, string processFilter, bool exactFolder = false)
        {
            ProcessFilter = processFilter ?? "";
            Folder = exactFolder ? root : Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(Folder);
        }
        public void Start(bool frames, bool deep = false)
        {
            Meta["version"] = "1.2.0";
            Meta["deepRequested"] = deep;
            Meta["startUtc"] = StartedUtc.ToString("o"); Meta["localStart"] = StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
            Meta["qpcOrigin"] = Origin; Meta["qpcFrequency"] = Stopwatch.Frequency;
            Meta["admin"] = IsAdmin; Meta["processFilter"] = ProcessFilter;
            Meta["logicalCpus"] = Environment.ProcessorCount; Meta["os"] = Environment.OSVersion.VersionString;
            Meta["traceSession"] = TraceName;
            Meta["limits"] = "2小时或会话日志总计512 MiB，先到者自动停止；剩余磁盘空间不足1 GiB也会停止。";
            Meta["hardware"] = Hardware();
            Json.Save(Path.Combine(Folder, "session.json"), Meta);
            probeThread = Worker("Scheduling probe", Probe);
            fastThread = Worker("System telemetry", FastLoop);
            slowThread = Worker("GPU and processes", SlowLoop);
            string unavailable = deep ? DeepTrace.Unavailable() : null;
            if (unavailable != null) { deep = false; Meta["deepSkipped"] = unavailable; Warn("事件级跟踪未启用（" + unavailable + "）；逐帧和系统记录照常进行。"); Json.Save(Path.Combine(Folder, "session.json"), Meta); }
            if (deep) { Deep = new DeepTrace(Folder, Origin, Warn); Deep.Start(); }
            if (frames) StartFrames(); else FrameStatus = "仅系统采集；未启用逐帧记录";
        }
        Thread Worker(string name, ThreadStart action)
        {
            var thread = new Thread(delegate() {
                try { action(); }
                catch (Exception ex) { Warn(name + ": " + ex.Message); StopReason = name + " 采集失败：" + ex.Message; stopped.Set(); }
            }) { IsBackground = true, Name = name };
            thread.Start(); return thread;
        }
        public void Warn(string message)
        {
            lock (logLock)
            {
                if (!warnings.Add(message)) return;
                try { File.AppendAllText(Path.Combine(Folder, "monitor.log"), DateTime.Now.ToString("o") + " " + message + Environment.NewLine, Encoding.UTF8); }
                catch { }
            }
        }
        void Probe()
        {
            long last = Stopwatch.GetTimestamp();
            while (!stopped.WaitOne(10))
            {
                long now = Stopwatch.GetTimestamp();
                double interval = (now - last) * 1000.0 / Stopwatch.Frequency;
                double late = Math.Max(0, interval - 10);
                long value = (long)(late * 1000), old;
                do { old = Interlocked.Read(ref maxProbeUs); if (old >= value) break; }
                while (Interlocked.CompareExchange(ref maxProbeUs, value, old) != old);
                if (late >= 25) probeGaps.Enqueue(new double[] { (now - Origin) / (double)Stopwatch.Frequency, interval, late });
                last = now;
            }
        }
        void FastLoop()
        {
            using (var metrics = new FastTelemetry())
            using (var output = Json.Writer(Path.Combine(Folder, "system.jsonl")))
            using (var gaps = Json.Writer(Path.Combine(Folder, "probe-gaps.jsonl")))
            using (var self = Process.GetCurrentProcess())
            {
                foreach (string e in metrics.Errors) Warn("不可用计数器：" + e);
                double last = Clock.Seconds(Origin), flushAt = last, lastCpu = self.TotalProcessorTime.TotalSeconds;
                int samples = 0;
                while (!stopped.WaitOne(250))
                {
                    double begin = Clock.Seconds(Origin);
                    var sample = metrics.Read();
                    sample.t = Clock.Seconds(Origin); sample.intervalMs = (sample.t - last) * 1000;
                    sample.probeLateMs = Interlocked.Exchange(ref maxProbeUs, 0) / 1000.0;
                    self.Refresh(); double cpu = self.TotalProcessorTime.TotalSeconds;
                    sample.monitorCpu = (cpu - lastCpu) / Math.Max(0.001, sample.t - last) / Environment.ProcessorCount * 100;
                    sample.monitorMemoryMB = self.WorkingSet64 / 1048576.0;
                    sample.costMs = (Clock.Seconds(Origin) - begin) * 1000;
                    last = sample.t; lastCpu = cpu;
                    if (Latest == null || Latest.foregroundPid != sample.foregroundPid) Interlocked.Exchange(ref foregroundSinceQpc, Stopwatch.GetTimestamp());
                    Latest = sample;
                    output.WriteLine(Json.Encode(sample)); samples++;
                    double[] gap; while (probeGaps.TryDequeue(out gap)) gaps.WriteLine(Json.Encode(gap));
                    if (sample.t - flushAt >= 1)
                    {
                        output.Flush(); gaps.Flush(); flushAt = sample.t;
                        if (sample.t >= 7200) StopReason = "已达到2小时会话上限";
                        if (Directory.GetFiles(Folder).Sum(p => new FileInfo(p).Length) >= 512L * 1024 * 1024) StopReason = "已达到512 MiB日志上限";
                        if (new DriveInfo(Path.GetPathRoot(Folder)).AvailableFreeSpace < 1024L * 1024 * 1024) StopReason = "剩余磁盘空间不足1 GiB";
                        if (StopReason != null) { stopped.Set(); break; }
                    }
                }
                double[] remaining; while (probeGaps.TryDequeue(out remaining)) gaps.WriteLine(Json.Encode(remaining));
                if (samples == 0) Warn("系统采样为空；会话过短或采集启动失败。");
            }
        }
        void SlowLoop()
        {
            using (var metrics = new SlowTelemetry())
            using (var output = Json.Writer(Path.Combine(Folder, "processes-gpu.jsonl")))
            {
                foreach (string e in metrics.Errors) Warn("不可用计数器：" + e);
                while (!stopped.WaitOne(1000))
                {
                    double start = Clock.Seconds(Origin);
                    var sample = metrics.Read(start);
                    sample.costMs = (Clock.Seconds(Origin) - start) * 1000;
                    LatestSlow = sample; output.WriteLine(Json.Encode(sample)); output.Flush();
                }
            }
        }
        void StartFrames()
        {
            if (!File.Exists(helper)) { FrameStatus = "缺少 PresentMon，正在仅记录系统"; Warn(FrameStatus); return; }
            string arguments = "--output_stdout --qpc_time --v1_metrics --no_track_input --session_name " + TraceName;
            if (ProcessFilter.Length > 0) arguments += " --process_name \"" + ProcessFilter + "\"";
            var info = new ProcessStartInfo(helper, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            try
            {
                var process = presentmon = Process.Start(info); FrameStatus = "正在等待游戏帧（游戏运行后应出现帧数）";
                stderrThread = Worker("PresentMon messages", delegate {
                    using (var output = new StreamWriter(new FileStream(Path.Combine(Folder, "presentmon.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)))
                    {
                        string line; int chars = 0;
                        while ((line = process.StandardError.ReadLine()) != null)
                        {
                            if (chars < 65536) { output.WriteLine(line); output.Flush(); chars += line.Length; }
                        }
                    }
                });
                frameThread = Worker("Frame recording", delegate {
                    using (var output = Json.Writer(Path.Combine(Folder, "frames.csv")))
                    {
                        string line; bool header = false; double flushAt = 0;
                        while ((line = process.StandardOutput.ReadLine()) != null)
                        {
                            if (!header)
                            {
                                if (!line.StartsWith("Application,", StringComparison.Ordinal)) { Warn("PresentMon: " + line); continue; }
                                header = true;
                                if (Deep != null) Deep.Header(line);
                            }
                            else {
                                Interlocked.Increment(ref FrameRows); FrameStatus = "逐帧记录正常";
                                if (Deep != null) Deep.Observe(line, Latest, (Interlocked.Read(ref foregroundSinceQpc) - Origin) / (double)Stopwatch.Frequency);
                            }
                            output.WriteLine(line);
                            double now = Clock.Seconds(Origin);
                            if (now - flushAt >= 1) { output.Flush(); flushAt = now; }
                        }
                    }
                    process.WaitForExit();
                    if (!stopped.WaitOne(0) && Interlocked.Read(ref FrameRows) == 0 && frameAttempts < 3)
                    {
                        // A failed ETW session start (seen as "access denied" even when elevated) is often transient: retry before giving up on frames.
                        frameAttempts++; FrameStatus = "逐帧记录启动失败（代码 " + process.ExitCode + "），第 " + frameAttempts + " 次重试";
                        Warn(FrameStatus); process.Dispose();
                        if (!stopped.WaitOne(3000)) StartFrames();
                        return;
                    }
                    if (!stopped.WaitOne(0))
                    {
                        FrameStatus = "逐帧记录已退出，代码 " + process.ExitCode + "；系统记录仍在继续";
                        Warn(FrameStatus + "。详见 presentmon.log；若 access denied，请使用管理员模式。");
                    }
                });
            }
            catch (Exception ex) { FrameStatus = "逐帧记录未启动；系统记录仍在继续"; Warn(FrameStatus + ": " + ex.Message); }
        }
        void StopFrames()
        {
            if (presentmon == null) return;
            try
            {
                if (!presentmon.HasExited)
                {
                    // Stop only OUR named ETW session. No Ctrl+C broadcast and no other recorder is touched.
                    using (var stop = Process.Start(new ProcessStartInfo(helper, "--session_name " + TraceName + " --terminate_existing_session") {
                        UseShellExecute = false, CreateNoWindow = true }))
                    {
                        if (!stop.WaitForExit(7000)) { stop.Kill(); Warn("PresentMon停止命令超时"); }
                    }
                    if (!presentmon.WaitForExit(7000)) { presentmon.Kill(); Warn("PresentMon未及时退出，已终止子进程；最后一段帧数据可能不完整。"); }
                }
                if (frameThread != null) frameThread.Join(10000);
                if (stderrThread != null) stderrThread.Join(10000);
            }
            catch (Exception ex) { Warn("停止逐帧记录：" + ex.Message); }
            finally { presentmon.Dispose(); }
        }
        public string StopAndReport()
        {
            DateTime endUtc = DateTime.UtcNow;
            double duration = Clock.Seconds(Origin);
            stopped.Set();
            if (Deep != null) Deep.RequestStop();
            foreach (var thread in new[] { probeThread, fastThread, slowThread })
                if (thread != null && !thread.Join(15000)) Warn(thread.Name + " 停止超时，报告可能不完整。");
            StopFrames();
            if (Deep != null && !Deep.Wait(240000)) Warn("深度跟踪仍在整理；请检查deep目录状态，不要重复启动记录器。");
            Meta["endUtc"] = endUtc.ToString("o"); Meta["duration"] = duration;
            Meta["collectionCompletedUtc"] = DateTime.UtcNow.ToString("o");
            Meta["stopReason"] = StopReason ?? "用户停止"; Meta["frameRows"] = Interlocked.Read(ref FrameRows); Meta["frameStatus"] = FrameStatus;
            lock (logLock) Meta["warnings"] = warnings.ToArray();
            Json.Save(Path.Combine(Folder, "session.json"), Meta);
            ExportEvents();
            return Report.Build(Folder);
        }
        void ExportEvents()
        {
            var events = new List<object>();
            try
            {
                string begin = StartedUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                string end = DateTime.Parse((string)Meta["endUtc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                string xpath = "*[System[TimeCreated[@SystemTime >= '" + begin + "' and @SystemTime <= '" + end + "'] and (Level=1 or Level=2 or Level=3)]]";
                using (var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath)))
                {
                    EventRecord record;
                    while ((record = reader.ReadEvent()) != null)
                    using (record)
                    {
                        string provider = record.ProviderName ?? "";
                        if (!(provider.IndexOf("WHEA", StringComparison.OrdinalIgnoreCase) >= 0 || provider.Equals("Display", StringComparison.OrdinalIgnoreCase)
                            || provider.Equals("disk", StringComparison.OrdinalIgnoreCase) || provider.IndexOf("stor", StringComparison.OrdinalIgnoreCase) >= 0
                            || provider.IndexOf("Kernel-Power", StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                        string message; try { message = record.FormatDescription(); } catch { message = "事件描述不可用"; }
                        events.Add(new { t = record.TimeCreated.HasValue ? (record.TimeCreated.Value.ToUniversalTime() - StartedUtc).TotalSeconds : 0,
                            provider = provider, id = record.Id, message = message });
                        if (events.Count >= 200) break;
                    }
                }
            }
            catch (Exception ex) { events.Add(new { t = 0, provider = "采集状态", id = 0, message = "读取系统事件失败：" + ex.Message }); }
            Json.Save(Path.Combine(Folder, "windows-events.json"), events);
        }
        static Dictionary<string, object> Hardware()
        {
            var result = new Dictionary<string, object>();
            foreach (var item in new[] { new[] { "cpu", "SELECT Name FROM Win32_Processor" }, new[] { "gpu", "SELECT Name,DriverVersion FROM Win32_VideoController" } })
            {
                try
                {
                    var rows = new List<Dictionary<string, string>>();
                    using (var search = new ManagementObjectSearcher(item[1]))
                    using (var found = search.Get())
                    foreach (ManagementObject row in found)
                    using (row)
                    {
                        var data = new Dictionary<string, string>();
                        foreach (PropertyData property in row.Properties) data[property.Name] = Convert.ToString(property.Value, CultureInfo.InvariantCulture);
                        rows.Add(data);
                    }
                    result[item[0]] = rows;
                }
                catch (Exception ex) { result[item[0]] = ex.Message; }
            }
            return result;
        }
    }
}
