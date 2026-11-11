using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace StutterScope
{
    public sealed class Incident
    {
        public double t, frameMs, baselineMs;
        public double? displayMs, gpuMs;
        public bool foreground, selfInduced;
        public string kind;
    }
    public sealed class Finding
    {
        public string level, title, detail; // level: warn | info | good
    }
    public sealed class FrameBucket
    {
        public double t, max, sum;
        public int count;
    }
    public sealed class FrameStream
    {
        public string key, application, swapChain;
        public int pid, frames, foregroundFrames, candidates, selfInduced;
        public double meanMs, p99Ms, maxMs;
        public string presentMode;
        public List<FrameBucket> timeline;
        public List<Incident> incidents;
    }
    sealed class StreamBuilder
    {
        public FrameStream result = new FrameStream { incidents = new List<Incident>() };
        public Dictionary<int, FrameBucket> buckets = new Dictionary<int, FrameBucket>();
        public SortedDictionary<int, int> histogram = new SortedDictionary<int, int>();
        public double baseline, sum, lastT = -1;
        public Dictionary<string, int> modes = new Dictionary<string, int>();
    }
    public sealed class ReportData
    {
        public Dictionary<string, object> meta;
        public List<Sample> samples;
        public List<SlowSample> slow;
        public List<double[]> gaps;
        public List<FrameStream> streams;
        public object events;
        public TraceState deep;
        public List<Finding> findings = new List<Finding>();
        public List<EtlSummary> traces = new List<EtlSummary>();
        public List<string> notes = new List<string>();
    }

    public static class Report
    {
        public static string[] Csv(string line)
        {
            var fields = new List<string>(); var field = new StringBuilder(); bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = !quoted;
                }
                else if (c == ',' && !quoted) { fields.Add(field.ToString()); field.Clear(); }
                else field.Append(c);
            }
            fields.Add(field.ToString()); return fields.ToArray();
        }
        static List<T> Lines<T>(string path, List<string> notes)
        {
            var result = new List<T>(); int bad = 0;
            if (!File.Exists(path)) { notes.Add("缺少 " + Path.GetFileName(path)); return result; }
            foreach (string line in File.ReadLines(path))
            {
                if (String.IsNullOrWhiteSpace(line)) continue;
                try { result.Add(Json.Decode<T>(line)); } catch { bad++; }
            }
            if (bad > 0) notes.Add(Path.GetFileName(path) + " 中有 " + bad + " 行不完整或无法解析，已跳过。");
            return result;
        }
        public static Sample Nearest(List<Sample> samples, double t)
        {
            if (samples.Count == 0) return null;
            int low = 0, high = samples.Count - 1;
            while (low < high) { int mid = (low + high) / 2; if (samples[mid].t < t) low = mid + 1; else high = mid; }
            if (low > 0 && Math.Abs(samples[low - 1].t - t) < Math.Abs(samples[low].t - t)) low--;
            return samples[low];
        }
        static double? Number(string[] row, Dictionary<string, int> columns, string key)
        {
            int index; double value;
            if (!columns.TryGetValue(key, out index) || index >= row.Length || !Double.TryParse(row[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !Pdh.Finite(value)) return null;
            return value;
        }
        public static ReportData Analyze(string folder)
        {
            var data = new ReportData();
            data.meta = Json.Decode<Dictionary<string, object>>(File.ReadAllText(Path.Combine(folder, "session.json")));
            data.samples = Lines<Sample>(Path.Combine(folder, "system.jsonl"), data.notes).OrderBy(s => s.t).ToList();
            data.slow = Lines<SlowSample>(Path.Combine(folder, "processes-gpu.jsonl"), data.notes).OrderBy(s => s.t).ToList();
            data.gaps = Lines<double[]>(Path.Combine(folder, "probe-gaps.jsonl"), data.notes);
            string eventPath = Path.Combine(folder, "windows-events.json");
            data.events = File.Exists(eventPath) ? Json.Decode<object>(File.ReadAllText(eventPath)) : new object[0];
            string deepPath = Path.Combine(folder, "deep", "deep-trace.json");
            if (File.Exists(deepPath))
            {
                try { data.deep = Json.Decode<TraceState>(File.ReadAllText(deepPath)); }
                catch { data.notes.Add("深度跟踪状态不完整；请直接检查deep目录中的ETL和wpr-commands.log。"); }
            }
            else if (data.meta.ContainsKey("deepRequested") && Convert.ToBoolean(data.meta["deepRequested"])) data.notes.Add("请求了深度跟踪，但没有状态文件；深度采集未确认成功。");
            if (data.deep != null && data.deep.error != null) data.notes.Add("深度跟踪：" + data.deep.error);
            if (!data.meta.ContainsKey("endUtc")) data.notes.Add("会话未正常结束；已从现有日志恢复报告，末尾数据可能不完整。");
            var builders = new Dictionary<string, StreamBuilder>();
            double endTime = data.meta.ContainsKey("endUtc") && data.meta.ContainsKey("duration") ? Convert.ToDouble(data.meta["duration"], CultureInfo.InvariantCulture) : Double.MaxValue;
            string csv = Path.Combine(folder, "frames.csv");
            int malformed = 0;
            if (File.Exists(csv))
            using (var reader = new StreamReader(csv))
            {
                string line = reader.ReadLine();
                var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (line != null)
                {
                    var header = Csv(line); for (int i = 0; i < header.Length; i++) columns[header[i].TrimStart('\uFEFF')] = i;
                }
                if (columns.ContainsKey("Application") && columns.ContainsKey("QPCTime") && columns.ContainsKey("msBetweenPresents"))
                {
                    long origin = Convert.ToInt64(data.meta["qpcOrigin"], CultureInfo.InvariantCulture);
                    double frequency = Convert.ToDouble(data.meta["qpcFrequency"], CultureInfo.InvariantCulture);
                    int modeColumn; if (!columns.TryGetValue("PresentMode", out modeColumn)) modeColumn = -1;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var row = Csv(line);
                        if (row.Length < columns.Count) { malformed++; continue; }
                        double? qpc = Number(row, columns, "QPCTime"), ft = Number(row, columns, "msBetweenPresents"), pidNumber = Number(row, columns, "ProcessID");
                        if (!qpc.HasValue || !ft.HasValue || !pidNumber.HasValue || ft <= 0) { malformed++; continue; }
                        double t = (qpc.Value - origin) / frequency;
                        if (t < 0 || t > endTime) continue;
                        int pid = (int)pidNumber.Value;
                        string app = row[columns["Application"]], chain = row[columns["SwapChainAddress"]];
                        string key = pid + "|" + chain;
                        StreamBuilder stream;
                        if (!builders.TryGetValue(key, out stream))
                        {
                            stream = new StreamBuilder(); builders[key] = stream;
                            stream.result.key = key; stream.result.application = app; stream.result.pid = pid; stream.result.swapChain = chain;
                        }
                        var result = stream.result;
                        var sample = Nearest(data.samples, t);
                        bool fg = sample != null && Math.Abs(sample.t - t) < 0.6 && sample.foregroundPid == pid;
                        result.frames++; if (fg) result.foregroundFrames++;
                        result.maxMs = Math.Max(result.maxMs, ft.Value); stream.sum += ft.Value;
                        int bin = (int)Math.Min(10000000, Math.Ceiling(ft.Value * 10));
                        int count; stream.histogram.TryGetValue(bin, out count); stream.histogram[bin] = count + 1;
                        int bucketId = (int)(t * 4); FrameBucket bucket;
                        if (!stream.buckets.TryGetValue(bucketId, out bucket))
                        { bucket = new FrameBucket { t = bucketId / 4.0 }; stream.buckets[bucketId] = bucket; }
                        bucket.max = Math.Max(bucket.max, ft.Value); bucket.sum += ft.Value; bucket.count++;
                        if (modeColumn >= 0 && fg) { int seen; stream.modes.TryGetValue(row[modeColumn], out seen); stream.modes[row[modeColumn]] = seen + 1; }
                        if (stream.baseline == 0) stream.baseline = ft.Value;
                        if (result.frames > 30 && ft >= Math.Max(12, stream.baseline * 2))
                        {
                            result.candidates++;
                            // A frame that spans the recorder's own WPR start or ETL export is not evidence about the game.
                            bool self = Disturbed(data.deep, t - ft.Value / 1000, t); if (self) result.selfInduced++;
                            result.incidents.Add(new Incident { t = t, frameMs = ft.Value, baselineMs = stream.baseline,
                                displayMs = Number(row, columns, "msBetweenDisplayChange"), gpuMs = Number(row, columns, "msGPUActive") ?? Number(row, columns, "msGPUDuration"),
                                foreground = fg, selfInduced = self, kind = self ? "监控自身干扰（跟踪启动/保存）" : ft >= 1000 ? "长帧 / 加载或切换" : "帧间隔突增" });
                        }
                        // Reset baseline after a long inactive gap, and resist a stall poisoning the baseline.
                        if (stream.lastT >= 0 && t - stream.lastT > 5) stream.baseline = Math.Min(ft.Value, 33.333);
                        else stream.baseline += 0.02 * (Math.Min(ft.Value, stream.baseline * 2) - stream.baseline);
                        stream.lastT = t;
                    }
                }
                else data.notes.Add("帧文件为空或格式不符；需要 PresentMon v1_metrics + qpc_time。检查 presentmon.log 和管理员权限。");
            }
            else data.notes.Add("本次没有逐帧文件；系统和手动标记仍可查看。完整帧记录需要管理员权限。");
            if (malformed > 0) data.notes.Add("帧文件中跳过 " + malformed + " 行无效或不完整的数据。");
            foreach (var stream in builders.Values)
            {
                var result = stream.result; result.meanMs = stream.sum / result.frames;
                if (stream.modes.Count > 0) result.presentMode = stream.modes.OrderByDescending(kv => kv.Value).First().Key;
                int rank = (int)Math.Ceiling(result.frames * 0.99), cumulative = 0;
                foreach (var kv in stream.histogram) { cumulative += kv.Value; if (cumulative >= rank) { result.p99Ms = kv.Key / 10.0; break; } }
                result.timeline = stream.buckets.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
                // Preserve total count; retain the 2000 largest incidents per stream in the self-contained report.
                result.incidents = result.incidents.OrderByDescending(e => e.frameMs).Take(2000).OrderBy(e => e.t).ToList();
            }
            data.streams = builders.Values.Select(b => b.result).OrderByDescending(s => s.foregroundFrames).ThenByDescending(s => s.frames).Take(16).ToList();
            if (builders.Count > 16) data.notes.Add("报告展示前台帧数最多的16条交换链；其他交换链仍保存在原始 frames.csv 中。");
            if (data.streams.Any(s => s.candidates > 2000)) data.notes.Add("每条交换链展示最长的2000次候选卡顿；原始帧未丢弃。");
            if (data.slow.Count > 0 && data.slow.All(s => !s.gpu.HasValue)) data.notes.Add("GPU性能计数器没有返回有效数据；不能把缺失数据解释成0%负载。");
            if (data.samples.Count > 0 && data.samples.All(s => !s.cpu.HasValue)) data.notes.Add("CPU性能计数器没有返回有效数据。");
            try { Traces(data, folder); } catch (Exception ex) { data.notes.Add("ETL自动解码失败：" + ex.Message); }
            try { Findings(data); } catch (Exception ex) { data.notes.Add("会话级诊断线索生成失败：" + ex.Message); }
            return data;
        }

        // ---- Session-level reading of the evidence. Every statement is a measured condition, not a proven cause. ----
        static readonly string[] Shell = { "dwm.exe", "explorer.exe", "StutterScope.exe", "SearchHost.exe", "ShellExperienceHost.exe" };
        public static bool Disturbed(TraceState deep, double from, double to)
        {
            if (deep == null) return false;
            foreach (var w in deep.exportWindows ?? new List<double[]>()) if (w.Length >= 2 && to >= w[0] - .25 && from <= w[1] + 1) return true;
            foreach (var w in deep.startWindows ?? new List<double[]>()) if (w.Length >= 2 && to >= w[0] - .25 && from <= w[1] + .75) return true;
            // Sessions recorded before startWindows existed: WPR start ended at recordingStart.
            foreach (var a in deep.artifacts ?? new List<TraceArtifact>()) if (to >= a.recordingStart - 1 && from <= a.recordingStart + .75) return true;
            return false;
        }
        static double Quantile(IEnumerable<double> values, double q)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted.Count == 0 ? Double.NaN : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))];
        }
        static string N(double value, int digits = 0) { return value.ToString("F" + digits, CultureInfo.InvariantCulture); }
        static string Stamp(double t) { return ((int)(t / 60)).ToString("00") + ":" + (t % 60).ToString("00.0", CultureInfo.InvariantCulture); }
        public static FrameStream Primary(ReportData data)
        {
            return data.streams == null ? null : data.streams.FirstOrDefault(s => s.foregroundFrames > 0 && !Shell.Any(n => n.Equals(s.application, StringComparison.OrdinalIgnoreCase)));
        }
        public static bool Significant(Incident e) { return e.foreground && !e.selfInduced && e.frameMs >= Math.Max(30, e.baselineMs * 4); }
        // Consecutive long frames within 3 s are one felt stutter.
        public static List<Incident> Stutters(FrameStream stream)
        {
            var result = new List<Incident>();
            foreach (var e in stream.incidents.Where(Significant).OrderBy(e => e.t))
            {
                if (result.Count > 0 && e.t - result[result.Count - 1].t < 3) { if (e.frameMs > result[result.Count - 1].frameMs) result[result.Count - 1] = e; }
                else result.Add(e);
            }
            return result;
        }
        static void Add(ReportData data, string level, string title, string detail) { data.findings.Add(new Finding { level = level, title = title, detail = detail }); }
        static void Findings(ReportData data)
        {
            var game = Primary(data);
            if (game == null) return;
            var samples = data.samples.Where(s => s.foregroundPid == game.pid).ToList();
            if (samples.Count < 20) samples = data.samples;
            double minutes = samples.Sum(s => s.intervalMs) / 60000;
            var stutters = Stutters(game);
            if (stutters.Count == 0) Add(data, "good", game.application + "：前台没有检测到明显卡顿", "判定标准：应用在前台、帧间隔 ≥ max(30 ms, 4×近期基线)。更小的抖动仍列在候选表中。");
            else
            {
                Add(data, "warn", game.application + "：前台明显卡顿 " + stutters.Count + " 次" + (minutes > .5 ? "（平均每 " + N(minutes * 60 / stutters.Count) + " 秒一次）" : ""),
                    "最长 " + N(stutters.Max(e => e.frameMs)) + " ms，中位 " + N(Quantile(stutters.Select(e => e.frameMs), .5)) + " ms；正常帧间隔约 " + N(game.meanMs, 1)
                    + " ms。判定标准：应用在前台、帧间隔 ≥ max(30 ms, 4×近期基线)，3 秒内连续的长帧算一次。");
                // Small hitches blur the rhythm of the big ones, so the large stalls are also tested on their own.
                foreach (double floor in new[] { 0.0, 100.0 })
                {
                    var subset = stutters.Where(e => e.frameMs >= floor).ToList();
                    var gaps = subset.Zip(subset.Skip(1), (a, b) => b.t - a.t).ToList();
                    if (gaps.Count < 3) continue;
                    double median = Quantile(gaps, .5); int near = gaps.Count(g => g >= median * .65 && g <= median * 1.35);
                    if (median >= 8 && near >= gaps.Count * .6)
                    {
                        Add(data, "warn", (floor > 0 ? "≥100 ms 的大卡顿" : "卡顿") + "有固定节奏：大约每 " + N(median) + " 秒一次（共 " + subset.Count + " 次）",
                            near + "/" + gaps.Count + " 个间隔落在 " + N(median * .65) + "–" + N(median * 1.35) + " 秒之间。按固定节奏出现的卡顿通常来自定时触发的动作"
                            + "（游戏或反作弊的定时扫描、后台程序轮询、驱动/服务的定时器），与画质和显卡负载关系不大；事件级跟踪可以指出卡顿时游戏主线程在等谁。");
                        break;
                    }
                }
                var gpu = stutters.Where(e => e.gpuMs.HasValue).Select(e => e.gpuMs.Value).ToList();
                if (gpu.Count > 0 && Quantile(gpu, .5) < Quantile(stutters.Select(e => e.frameMs), .5) * .25)
                    Add(data, "info", "卡顿帧里显卡只忙了约 " + N(Quantile(gpu, .5), 1) + " ms：不是显卡渲染不过来",
                        "长帧的时间花在提交这一帧之前（游戏线程在计算或等待），降低画质对这类卡顿帮助有限。");
            }
            foreach (var trace in data.traces)
                foreach (var stall in trace.stalls.Where(s => s.covered && s.conclusions.Count > 0))
                    Add(data, "warn", "事件级跟踪 " + Stamp(stall.t) + "（" + N(stall.frameMs) + " ms 长帧，" + trace.file + "）", String.Join("\n", stall.conclusions));
            int self = game.selfInduced;
            if (self > 0) Add(data, "info", self + " 个候选长帧发生在事件级跟踪启动或保存期间", "它们是监控自身造成的，已标为“监控自身干扰”，不计入上面的卡顿统计，默认也不显示在候选表里。");

            var perf = samples.Where(s => s.cpuPerformance.HasValue).Select(s => s.cpuPerformance.Value).ToList();
            var core = samples.Where(s => s.coreMax.HasValue).Select(s => s.coreMax.Value).ToList();
            if (perf.Count >= 20 && Quantile(perf, .5) < 85 && Quantile(perf, .95) - Quantile(perf, .05) < 6)
            {
                var mhz = samples.Where(s => s.cpuMHz.HasValue).Select(s => s.cpuMHz.Value).ToList();
                Add(data, "warn", "CPU 频率被限制在标称的 " + N(Quantile(perf, .5)) + "%" + (mhz.Count > 0 ? "（约 " + N(Quantile(mhz, .5)) + " MHz）" : ""),
                    "整段记录里“处理器性能百分比”几乎不变，说明存在频率上限，而不是随负载升降。常见来源：厂商控制中心的安静/均衡模式、电源计划的“最大处理器状态”、省电模式。"
                    + "不受限时该值在游戏中通常高于 100%（睿频）。" + (samples.Any(s => s.acPower == 0) ? "本次有一部分时间使用电池供电。" : ""));
            }
            if (core.Count >= 20 && Quantile(core, .5) >= 85)
                Add(data, "warn", "最忙的逻辑核心长期接近满载（中位 " + N(Quantile(core, .5)) + "%）", "游戏主线程已经吃满一个核心时，任何额外工作（后台程序抢占、定时扫描）都会直接变成长帧。单核频率比核心数量更重要。");
            var commit = samples.Where(s => s.commitPct.HasValue).Select(s => s.commitPct.Value).ToList();
            var available = samples.Where(s => s.availableMB.HasValue).Select(s => s.availableMB.Value).ToList();
            if (commit.Count >= 20 && (Quantile(commit, .5) >= 85 || Quantile(available, .05) < 1536))
            {
                var pages = samples.Where(s => s.pagesIn.HasValue).Select(s => s.pagesIn.Value).ToList();
                Add(data, "warn", "内存紧张：提交量中位 " + N(Quantile(commit, .5)) + "%，可用内存低位 " + N(Quantile(available, .05)) + " MB",
                    "页面读入 P90 " + N(Quantile(pages, .9)) + " 页/秒。内存不足时系统会压缩和换出页面，游戏或被它等待的后台进程再次用到这些页面就要等磁盘。关闭占内存的后台程序（浏览器、聊天/AI 客户端等）。");
            }
            if (game.presentMode != null && game.presentMode.IndexOf("Composed: Copy", StringComparison.OrdinalIgnoreCase) >= 0)
                Add(data, "info", "游戏以窗口化“复制合成”方式呈现（" + game.presentMode + "）",
                    "每一帧都先交给桌面合成器再拷贝上屏，延迟和抖动高于独占全屏或翻转模型，也更容易被其他窗口的活动牵连。可尝试游戏内的独占全屏，或在 Windows 设置 › 系统 › 屏幕 › 显示卡 中开启“窗口化游戏优化”。");
            if (data.slow.Count >= 10)
            {
                string own = Path.GetFileNameWithoutExtension(game.application);
                var busy = data.slow.SelectMany(s => s.processes).Where(p => p.pid != game.pid && !p.name.Equals(own, StringComparison.OrdinalIgnoreCase) && !p.name.Equals("StutterScope", StringComparison.OrdinalIgnoreCase)
                        && !p.name.StartsWith("PresentMon", StringComparison.OrdinalIgnoreCase) && !p.name.Equals("wpr", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(p => p.name).Select(g => new { name = g.Key, cpu = g.Sum(p => p.cpu) / data.slow.Count * Environment.ProcessorCount / 100 })
                    .Where(p => p.cpu >= .05).OrderByDescending(p => p.cpu).Take(6).ToList();
                if (busy.Count > 0)
                    Add(data, "info", "后台持续占用 CPU 的进程", String.Join("，", busy.Select(p => p.name + " ≈ " + N(p.cpu, 2) + " 核")) + "。数值是整段记录的平均（按本机逻辑核心数折算，只统计进入每秒前6名的时刻，偏保守）。");
            }
        }

        // Decode every saved ETL around its trigger and around the other long frames it still retains.
        static void Traces(ReportData data, string folder)
        {
            if (data.deep == null || data.deep.artifacts == null) return;
            long origin = Convert.ToInt64(data.meta["qpcOrigin"], CultureInfo.InvariantCulture);
            double frequency = Convert.ToDouble(data.meta["qpcFrequency"], CultureInfo.InvariantCulture);
            var game = Primary(data);
            foreach (var artifact in data.deep.artifacts)
            {
                if (artifact.file == null || artifact.bytes < 1024 * 1024) continue;
                string path = Path.Combine(folder, artifact.file.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) continue;
                var windows = new List<StallWindow>();
                var trigger = artifact.trigger;
                if (trigger != null && trigger.source == "frame" && trigger.pid != 0)
                    windows.Add(new StallWindow { t = trigger.t, frameMs = trigger.frameMs, pid = trigger.pid, application = trigger.application, source = "自动触发" });
                int pid = windows.Count > 0 ? windows[0].pid : game == null ? 0 : game.pid;
                var stream = data.streams.Where(s => s.pid == pid).OrderByDescending(s => s.foregroundFrames).FirstOrDefault();
                if (stream != null)
                    foreach (var e in stream.incidents.Where(e => e.foreground && !e.selfInduced && e.frameMs >= 30 && e.t > artifact.stopRequestedAt - 7 && e.t < artifact.stopRequestedAt - .2).OrderByDescending(e => e.frameMs))
                    {
                        if (windows.Count >= 3) break;
                        if (windows.All(w => Math.Abs(w.t - e.t) > .3)) windows.Add(new StallWindow { t = e.t, frameMs = e.frameMs, pid = pid, application = stream.application, source = "同一份跟踪内的其他长帧" });
                    }
                if (windows.Count == 0) continue;
                string cache = Path.ChangeExtension(path, null) + "-summary.json";
                EtlSummary summary = null;
                try { if (File.Exists(cache)) { summary = Json.Decode<EtlSummary>(File.ReadAllText(cache)); if (summary.schema != EtlSummary.Schema || summary.stalls.Count != windows.Count) summary = null; } } catch (Exception) { summary = null; }
                if (summary == null)
                {
                    summary = EtlAnalyzer.Analyze(path, origin, frequency, windows);
                    try { Json.Save(cache, summary); } catch (IOException) { }
                }
                if (summary.error != null) data.notes.Add(artifact.file + " 自动解码失败：" + summary.error);
                data.traces.Add(summary);
            }
        }
        public static string Build(string folder)
        {
            var data = Analyze(folder);
            var summary = new { meta = data.meta, notes = data.notes, findings = data.findings, deep = data.deep, traces = data.traces, streams = data.streams.Select(s => new {
                s.application, s.pid, s.swapChain, s.presentMode, s.frames, s.foregroundFrames, s.candidates, s.selfInduced, s.meanMs, s.p99Ms, s.maxMs, s.incidents }) };
            Json.Save(Path.Combine(folder, "analysis.json"), summary);
            string template = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "report-template.html"));
            // Data is never interpreted as HTML or JavaScript. Escape HTML raw-text terminators too.
            string encoded = Json.Encode(data).Replace("<", "\\u003c").Replace(">", "\\u003e").Replace("&", "\\u0026").Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029");
            string path = Path.Combine(folder, "report.html");
            File.WriteAllText(path, template.Replace("__REPORT_DATA__", encoded), new UTF8Encoding(false));
            return path;
        }
    }
}
