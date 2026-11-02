using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace StutterScope
{
    public sealed class TraceTrigger
    {
        public double t, observedAt, frameMs, baselineMs;
        public int pid;
        public string application, swapChain, source;
    }
    public sealed class TraceArtifact
    {
        public string file, reason, savedUtc, status;
        public double recordingStart, stopRequestedAt, saveCompletedAt;
        public long bytes;
        public long? eventsLost;
        public TraceTrigger trigger;
    }
    public sealed class TraceState
    {
        public bool requested, active;
        public string status, instanceName, error;
        public int configuredBufferMB = 256;
        public double automaticThresholdMs = 50;
        public List<TraceArtifact> artifacts = new List<TraceArtifact>();
        public List<double[]> exportWindows = new List<double[]>();
    }

    // Does not equate a 250ms CPU sample with a driver's single execution time.
    // Qualifies frame events for a separate event-level recording instead.
    public sealed class FrameTriggerDetector
    {
        sealed class History { public int count; public double baseline, lastT; }
        readonly Dictionary<string, History> histories = new Dictionary<string, History>();
        Dictionary<string, int> columns;
        public void Header(string header)
        {
            var names = Report.Csv(header); columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Length; i++) columns[names[i]] = i;
            foreach (var key in new[] { "Application", "ProcessID", "QPCTime", "msBetweenPresents", "SwapChainAddress" })
                if (!columns.ContainsKey(key)) { columns = null; break; }
        }
        public TraceTrigger Observe(string line, long origin, double frequency, double now, double armedAt, Sample foreground, double foregroundSince)
        {
            if (columns == null) return null;
            var row = Report.Csv(line); if (row.Length != columns.Count) return null;
            int pid; double frame; long qpc;
            if (!Int32.TryParse(row[columns["ProcessID"]], out pid)
                || !Int64.TryParse(row[columns["QPCTime"]], out qpc)
                || !Double.TryParse(row[columns["msBetweenPresents"]], NumberStyles.Float, CultureInfo.InvariantCulture, out frame)
                || !Pdh.Finite(frame) || frame <= 0) return null;
            double t = (qpc - origin) / frequency;
            string application = row[columns["Application"]], chain = row[columns["SwapChainAddress"]];
            string key = pid + "|" + chain; History history;
            if (!histories.TryGetValue(key, out history))
            { history = new History { baseline = Math.Min(frame, 33.333), lastT = t }; histories[key] = history; }
            if (t < history.lastT || t - history.lastT > 5) { history.count = 0; history.baseline = Math.Min(frame, 33.333); }
            double baseline = history.baseline;
            bool qualifies = history.count >= 120 && frame >= Math.Max(50, baseline * 4);
            history.count++; history.lastT = t;
            history.baseline += .02 * (Math.Min(frame, baseline * 2) - baseline);
            if (!qualifies || t < armedAt + 10 || now - t > 5 || t > now + .1) return null;
            if (foreground == null || foreground.foregroundPid != pid || now - foreground.t > 1 || t - foregroundSince < 3) return null;
            if (application.Equals("dwm.exe", StringComparison.OrdinalIgnoreCase) || application.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
                || application.Equals("StutterScope.exe", StringComparison.OrdinalIgnoreCase) || application.Equals("SearchHost.exe", StringComparison.OrdinalIgnoreCase)) return null;
            return new TraceTrigger { t = t, observedAt = now, frameMs = frame, baselineMs = baseline, pid = pid, application = application, swapChain = chain, source = "frame" };
        }
    }

    public sealed class CommandResult
    {
        public int code;
        public bool timedOut;
        public string output;
    }
    public interface ITraceCommands
    {
        CommandResult Run(string arguments, int timeoutMs);
    }
    public sealed class WprCommands : ITraceCommands
    {
        readonly string folder;
        public WprCommands(string folder) { this.folder = folder; }
        public CommandResult Run(string arguments, int timeoutMs)
        {
            var text = new StringBuilder(); var gate = new object();
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wpr.exe");
            using (var process = new Process { StartInfo = new ProcessStartInfo(exe, arguments) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } })
            {
                DataReceivedEventHandler receive = delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data != null) lock (gate) { if (text.Length < 131072) text.AppendLine(e.Data); }
                };
                process.OutputDataReceived += receive; process.ErrorDataReceived += receive;
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                bool exited = process.WaitForExit(timeoutMs);
                if (!exited) { try { process.Kill(); } catch (InvalidOperationException) { } }
                process.WaitForExit();
                var result = new CommandResult { code = exited ? process.ExitCode : -1, timedOut = !exited, output = text.ToString() };
                File.AppendAllText(Path.Combine(folder, "wpr-commands.log"), DateTime.Now.ToString("o") + " wpr " + arguments + "\nexit=" + result.code + " timeout=" + result.timedOut + "\n" + result.output + "\n", new UTF8Encoding(false));
                return result;
            }
        }
    }

    public sealed class DeepTrace
    {
        readonly string folder;
        readonly long origin;
        readonly ITraceCommands commands;
        readonly Action<string> warn;
        readonly bool allowed;
        readonly object sync = new object();
        readonly ManualResetEvent closing = new ManualResetEvent(false);
        readonly FrameTriggerDetector detector = new FrameTriggerDetector();
        readonly TraceState state;
        Thread thread;
        TraceTrigger pending;
        volatile bool armed;
        double armedAt;
        bool ownSessionMayExist;
        public const int MaxArtifacts = 3;
        public string Status { get { lock (sync) return state.status; } }
        public bool Armed { get { return armed; } }
        public string InstanceName { get { return state.instanceName; } }

        public DeepTrace(string folder, long origin, Action<string> warn, ITraceCommands commands = null, bool? allow = null)
        {
            this.folder = Path.Combine(folder, "deep"); Directory.CreateDirectory(this.folder);
            this.origin = origin; this.warn = warn; this.commands = commands ?? new WprCommands(this.folder);
            allowed = allow ?? Session.IsAdmin;
            state = new TraceState { requested = true, instanceName = "StutterScope_" + Guid.NewGuid().ToString("N"), status = "深度跟踪正在启动" };
        }
        void Persist()
        {
            // Reader uses FileShare.Read. A crash can leave the last JSON incomplete; ETLs remain independent.
            Json.Save(Path.Combine(folder, "deep-trace.json"), state);
        }
        void StatusTo(string value, bool active)
        {
            lock (sync) { state.status = value; state.active = active; Persist(); }
        }
        string Instance { get { return " -instancename " + state.instanceName; } }
        static string Quote(string value) { return "\"" + value.Replace("\"", "") + "\""; }
        double Now { get { return Clock.Seconds(origin); } }
        public void Start()
        {
            StatusTo(state.status, false);
            thread = new Thread(Run) { IsBackground = true, Name = "WPR deep trace" }; thread.Start();
        }
        public void Header(string line) { detector.Header(line); }
        public void Observe(string line, Sample sample, double stableSince)
        {
            if (!armed) return;
            var trigger = detector.Observe(line, origin, Stopwatch.Frequency, Now, armedAt, sample, stableSince);
            if (trigger != null) Trigger(trigger);
        }
        public void Trigger(TraceTrigger trigger)
        {
            lock (sync)
            {
                if (!armed || closing.WaitOne(0)) return;
                if (pending == null || trigger.frameMs > pending.frameMs) pending = trigger;
            }
        }
        bool BudgetAvailable()
        {
            long bytes; int count;
            lock (sync) { bytes = state.artifacts.Sum(a => a.bytes); count = state.artifacts.Count; }
            if (count >= MaxArtifacts || bytes + 512L * 1024 * 1024 > 1024L * 1024 * 1024)
            { StatusTo("深度跟踪已达到保存上限；基础记录继续", false); return false; }
            if (new DriveInfo(Path.GetPathRoot(folder)).AvailableFreeSpace < 2L * 1024 * 1024 * 1024)
            { StatusTo("磁盘可用空间不足2 GiB，未继续深度跟踪", false); return false; }
            return true;
        }
        void Run()
        {
            try
            {
                if (!allowed) throw new InvalidOperationException("深度跟踪需要管理员权限");
                while (!closing.WaitOne(0) && BudgetAvailable())
                {
                    StatusTo("正在启动256 MB循环跟踪", false);
                    string profile = Path.Combine(Program.Home, "DeepTrace.wprp");
                    ownSessionMayExist = true;
                    var started = commands.Run("-start " + Quote(profile + "!StutterScope") + Instance, 45000);
                    if (started.code != 0) throw new InvalidOperationException("WPR启动失败：" + started.code + " " + started.output.Trim());
                    armedAt = Now;
                    lock (sync) { pending = null; armed = true; }
                    StatusTo("深度循环跟踪中，已保存" + state.artifacts.Count + "/3", true);
                    double firstObserved = -1;
                    while (!closing.WaitOne(100))
                    {
                        bool hasPending; lock (sync) hasPending = pending != null;
                        if (hasPending && firstObserved < 0) firstObserved = Now;
                        if (firstObserved >= 0 && Now - firstObserved >= 1) break;
                    }
                    TraceTrigger trigger;
                    lock (sync) { armed = false; trigger = pending; pending = null; }
                    Save(trigger, trigger != null && trigger.source == "self-test" ? "保存流程自测（非真实卡顿）" : closing.WaitOne(0) ? "关闭记录窗口" : "前台长帧自动触发");
                    if (closing.WaitOne(0)) break;
                    StatusTo("跟踪已保存，10秒冷却后恢复；基础记录继续", false);
                    if (closing.WaitOne(10000)) break;
                }
                if (closing.WaitOne(0)) StatusTo("深度跟踪已停止，已保存" + state.artifacts.Count + "份", false);
            }
            catch (Exception ex)
            {
                lock (sync) { state.error = ex.Message; }
                warn(ex.Message); StatusTo("深度跟踪失败；基础记录继续，详见deep目录日志", false);
            }
            finally
            {
                armed = false;
                if (ownSessionMayExist)
                {
                    try
                    {
                        // The instance suffix is mandatory. Never cancel another tool's WPR session.
                        var result = commands.Run("-cancel" + Instance, 30000);
                        if (result.code != 0) warn("本轮WPR清理未确认成功；使用deep-trace.json中的instanceName检查，详见wpr-commands.log");
                        else ownSessionMayExist = false;
                    }
                    catch (Exception ex) { warn("WPR清理失败：" + ex.Message); }
                }
            }
        }
        void Save(TraceTrigger trigger, string reason)
        {
            double exportStart = Now;
            string file = String.Format(CultureInfo.InvariantCulture, "trace-{0:00}.etl", state.artifacts.Count + 1);
            var artifact = new TraceArtifact { file = "deep/" + file, reason = reason, trigger = trigger, recordingStart = armedAt,
                stopRequestedAt = exportStart, status = "正在保存" };
            lock (sync) { state.artifacts.Add(artifact); state.status = "正在保存深度跟踪，基础记录继续"; Persist(); }
            var status = commands.Run("-status collectors -details" + Instance, 15000);
            File.WriteAllText(Path.Combine(folder, "trace-" + state.artifacts.Count.ToString("00") + "-status.txt"), status.output, new UTF8Encoding(false));
            var losses = Regex.Matches(status.output, @"Events Lost\s*:\s*(\d+)");
            if (status.code == 0 && losses.Count > 0) artifact.eventsLost = losses.Cast<Match>().Sum(m => Int64.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            var saved = commands.Run("-stop " + Quote(Path.Combine(folder, file)) + " -skipPdbGen" + Instance, 120000);
            if (saved.code != 0) { artifact.status = "保存失败"; throw new InvalidOperationException("WPR保存失败：" + saved.code + " " + saved.output.Trim()); }
            ownSessionMayExist = false;
            artifact.saveCompletedAt = Now; artifact.savedUtc = DateTime.UtcNow.ToString("o");
            string path = Path.Combine(folder, file);
            if (!File.Exists(path) || new FileInfo(path).Length < 1024) { artifact.status = "文件缺失或过小"; throw new IOException("WPR未生成有效ETL文件"); }
            artifact.bytes = new FileInfo(path).Length; artifact.status = "已保存";
            lock (sync) { state.active = false; state.exportWindows.Add(new[] { exportStart, artifact.saveCompletedAt }); Persist(); }
            if (artifact.eventsLost > 0) warn("深度ETW跟踪报告丢失事件：" + artifact.eventsLost + "，该段分析可能不完整。");
        }
        public void RequestStop() { closing.Set(); }
        public bool Wait(int milliseconds) { return thread == null || thread.Join(milliseconds); }
    }
}
