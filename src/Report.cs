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
        public bool foreground;
        public string kind;
    }
    public sealed class FrameBucket
    {
        public double t, max, sum;
        public int count;
    }
    public sealed class FrameStream
    {
        public string key, application, swapChain;
        public int pid, frames, foregroundFrames, candidates;
        public double meanMs, p99Ms, maxMs;
        public List<FrameBucket> timeline;
        public List<Incident> incidents;
    }
    sealed class StreamBuilder
    {
        public FrameStream result = new FrameStream { incidents = new List<Incident>() };
        public Dictionary<int, FrameBucket> buckets = new Dictionary<int, FrameBucket>();
        public SortedDictionary<int, int> histogram = new SortedDictionary<int, int>();
        public double baseline, sum, lastT = -1;
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
                        if (stream.baseline == 0) stream.baseline = ft.Value;
                        if (result.frames > 30 && ft >= Math.Max(12, stream.baseline * 2))
                        {
                            result.candidates++;
                            result.incidents.Add(new Incident { t = t, frameMs = ft.Value, baselineMs = stream.baseline,
                                displayMs = Number(row, columns, "msBetweenDisplayChange"), gpuMs = Number(row, columns, "msGPUActive") ?? Number(row, columns, "msGPUDuration"),
                                foreground = fg, kind = ft >= 1000 ? "长帧 / 加载或切换" : "帧间隔突增" });
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
            return data;
        }
        public static string Build(string folder)
        {
            var data = Analyze(folder);
            var summary = new { meta = data.meta, notes = data.notes, deep = data.deep, streams = data.streams.Select(s => new {
                s.application, s.pid, s.swapChain, s.frames, s.foregroundFrames, s.candidates, s.meanMs, s.p99Ms, s.maxMs, s.incidents }) };
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
