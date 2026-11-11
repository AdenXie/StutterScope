using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Text.RegularExpressions;

namespace StutterScope
{
    public static class SelfTest
    {
        static void Assert(bool condition, string message) { if (!condition) throw new Exception("TEST FAILED: " + message); Console.WriteLine("PASS " + message); }
        public static int Run()
        {
            var csv = Report.Csv("\"demo, app\",\"one\"\"two\",3");
            Assert(csv.Length == 3 && csv[0] == "demo, app" && csv[1] == "one\"two", "CSV quotes and commas");
            var nullable = Json.Decode<Sample>(Json.Encode(new Sample { t = 1, cpu = null, coreMax = 95 }));
            Assert(nullable.cpu == null && nullable.coreMax == 95, "Missing counters remain null");
            TriggerTests();
            string folder = Path.Combine(Program.Home, "tests", "output", "fixture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
            Directory.CreateDirectory(folder);
            var meta = new Dictionary<string, object> { { "startUtc", DateTime.UtcNow.ToString("o") }, { "qpcOrigin", 1000000L }, { "qpcFrequency", 10000000L }, { "duration", 3 } };
            Json.Save(Path.Combine(folder, "session.json"), meta);
            using (var output = Json.Writer(Path.Combine(folder, "system.jsonl")))
            {
                for (int i = 0; i < 13; i++) output.WriteLine(Json.Encode(new Sample { t = i * .25, cpu = 20, coreMax = 70, foregroundPid = 42, foreground = "demo" }));
                output.WriteLine("{truncated");
            }
            using (var output = Json.Writer(Path.Combine(folder, "processes-gpu.jsonl"))) output.WriteLine(Json.Encode(new SlowSample { t = 1, gpu = null }));
            File.WriteAllText(Path.Combine(folder, "probe-gaps.jsonl"), "");
            Json.Save(Path.Combine(folder, "windows-events.json"), new object[0]);
            using (var output = Json.Writer(Path.Combine(folder, "frames.csv")))
            {
                // Header copied from pinned PresentMon 2.6.0 v1 gold output; input column omitted by --no_track_input.
                output.WriteLine("Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,msInPresentAPI,msBetweenPresents,AllowsTearing,PresentMode,msUntilRenderComplete,msUntilDisplayed,msBetweenDisplayChange,msFlipDelay,msUntilRenderStart,msGPUActive,msGPUVideoActive,QPCTime");
                double t = 0;
                for (int i = 0; i < 121; i++)
                {
                    double ft = i == 120 ? 80 : 8.333;
                    t += ft / 1000;
                    output.WriteLine("\"demo,</script>.exe\",42,0xAAA,DXGI,0,0,0,0,0," + ft.ToString(CultureInfo.InvariantCulture)
                        + ",1,Composed: Flip,4,8,8,0,0,4,0," + (1000000L + (long)Math.Round(t * 10000000)).ToString(CultureInfo.InvariantCulture));
                }
                for (int i = 1; i <= 60; i++) output.WriteLine("demo.exe,42,0xBBB,DXGI,0,0,0,0,0,16.667,1,Composed: Flip,4,8,8,0,0,4,0," + (1000000L + i * 166670L));
                output.WriteLine("truncated,row");
            }
            var data = Report.Analyze(folder);
            Assert(data.streams.Count == 2, "Swap chains stay separate");
            var main = data.streams.Single(s => s.swapChain == "0xAAA");
            Assert(main.frames == 121 && main.candidates == 1 && main.maxMs == 80, "Known 80ms hitch detected exactly once");
            Assert(Report.Stutters(main).Count == 1 && !main.incidents[0].selfInduced && main.presentMode == "Composed: Flip", "Hitch counts as one foreground stutter; present mode kept");
            Assert(data.findings.Any(f => f.level == "warn" && f.title.Contains("明显卡顿 1 次")), "Session findings summarise the stutter");
            var exporting = new TraceState { exportWindows = new List<double[]> { new[] { 10.0, 40.0 } }, startWindows = new List<double[]> { new[] { 69.6, 70.2 } } };
            Assert(Report.Disturbed(exporting, 12, 12.05) && Report.Disturbed(exporting, 69.9, 70.15) && !Report.Disturbed(exporting, 50, 50.2), "Frames inside the recorder's own WPR start/export are flagged");
            Assert(Math.Abs(main.incidents[0].t - 1.07996) < .00001 && main.incidents[0].foreground, "QPC aligns to foreground telemetry");
            Assert(main.incidents[0].gpuMs == 4, "Pinned v1 GPU-active column parsed");
            Assert(main.p99Ms == 8.4, "P99 upper histogram bin");
            Assert(data.notes.Any(n => n.Contains("未正常结束")) && data.notes.Any(n => n.Contains("不完整")), "Crash recovery reports truncated data");
            string report = Report.Build(folder), html = File.ReadAllText(report);
            Assert(!html.Contains("demo,</script>.exe") && html.Contains("demo,\\u003c/script\\u003e.exe"), "Untrusted process names cannot close data script");
            Assert(File.Exists(Path.Combine(folder, "analysis.json")), "Offline report and analysis JSON generated");
            TraceLifecycleTests(folder);
            string official = Path.Combine(Program.Home, "tests", "presentmon-official-v1.csv");
            if (File.Exists(official))
            {
                string officialFolder = Path.Combine(folder, "official"); Directory.CreateDirectory(officialFolder);
                Json.Save(Path.Combine(officialFolder, "session.json"), meta);
                File.Copy(official, Path.Combine(officialFolder, "frames.csv"));
                var parsed = Report.Analyze(officialFolder);
                Assert(parsed.streams.Count > 0 && parsed.streams.Sum(s => s.frames) > 30, "Actual upstream PresentMon gold CSV accepted");
            }
            Console.WriteLine("Synthetic fixture (not a diagnosis): " + report);
            return 0;
        }
        static void TriggerTests()
        {
            var detector = new FrameTriggerDetector();
            detector.Header("Application,ProcessID,SwapChainAddress,msBetweenPresents,QPCTime");
            for (int i = 0; i < 130; i++)
                detector.Observe("game.exe,42,0xA,8," + (10000 + i * 8), 0, 1000, 10 + i * .008, 0,
                    new Sample { t = 10 + i * .008, foregroundPid = 42 }, 0);
            Func<int, double, double, double, TraceTrigger> observe = (pid, now, armed, since) => detector.Observe("game.exe,42,0xA,80,11120", 0, 1000, now, armed, new Sample { t = now, foregroundPid = pid }, since);
            Assert(observe(42, 11.32, 0, 0) != null, "Deep trigger accepts stable foreground long frame");
            Assert(observe(43, 11.32, 0, 0) == null, "Background frames cannot trigger deep save");
            Assert(observe(42, 11.32, 5, 0) == null, "WPR startup warmup is excluded");
            Assert(observe(42, 11.32, 0, 10) == null, "Recent focus switches cannot trigger deep save");
            Assert(observe(42, 18, 0, 0) == null, "Stale PresentMon delivery cannot trigger deep save");
        }
        sealed class FakeWpr : ITraceCommands
        {
            public bool failStart;
            public List<string> calls = new List<string>();
            public CommandResult Run(string args, int timeout)
            {
                lock (calls) calls.Add(args);
                if (args.StartsWith("-start") && failStart) return new CommandResult { code = 5, output = "access denied (test)" };
                if (args.StartsWith("-stop"))
                {
                    string path = Regex.Match(args, "^-stop \\\"([^\\\"]+)\\\"").Groups[1].Value;
                    File.WriteAllBytes(path, new byte[4096]); // A fixture only; real ETL validation uses the administrator test.
                }
                return new CommandResult { code = 0, output = "Events Lost : 2\n" };
            }
        }
        static bool Until(Func<bool> condition, int timeout)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeout) { if (condition()) return true; Thread.Sleep(20); }
            return condition();
        }
        static void TraceLifecycleTests(string folder)
        {
            var fake = new FakeWpr(); var warnings = new List<string>();
            var trace = new DeepTrace(folder, Stopwatch.GetTimestamp(), warnings.Add, fake, true);
            trace.Start(); Assert(Until(() => trace.Armed, 3000), "Deep lifecycle arms memory capture");
            trace.Trigger(new TraceTrigger { frameMs = 80, source = "self-test" });
            Assert(Until(() => File.Exists(Path.Combine(folder, "deep", "trace-01.etl")), 5000), "Automatic trigger saves a snapshot");
            trace.RequestStop(); Assert(trace.Wait(5000), "Closing during cooldown exits without restarting");
            var state = Json.Decode<TraceState>(File.ReadAllText(Path.Combine(folder, "deep", "deep-trace.json")));
            Assert(state.artifacts.Count == 1 && state.artifacts[0].eventsLost == 2 && state.exportWindows.Count == 1, "ETL index preserves event loss and export interval");
            Assert(fake.calls.All(c => c.EndsWith("-instancename " + trace.InstanceName)), "Every WPR action targets only the owned instance");
            var failureFolder = Path.Combine(folder, "failed-deep"); Directory.CreateDirectory(failureFolder);
            var failing = new FakeWpr { failStart = true };
            var failed = new DeepTrace(failureFolder, Stopwatch.GetTimestamp(), delegate { }, failing, true);
            failed.Start(); Assert(failed.Wait(3000), "Startup failure exits cleanly");
            var error = Json.Decode<TraceState>(File.ReadAllText(Path.Combine(failureFolder, "deep", "deep-trace.json")));
            Assert(error.error != null && !failed.Armed && failing.calls.Any(c => c.StartsWith("-cancel -instancename ")), "Failed startup reports error and cleans only its named instance");
            string closeFolder = Path.Combine(folder, "closed-deep"); Directory.CreateDirectory(closeFolder);
            var closeFake = new FakeWpr();
            var closing = new DeepTrace(closeFolder, Stopwatch.GetTimestamp(), delegate { }, closeFake, true);
            closing.Start(); Assert(Until(() => closing.Armed, 3000), "Close test starts an active trace");
            closing.RequestStop(); Assert(closing.Wait(5000), "Closing an idle trace exits");
            var closed = Json.Decode<TraceState>(File.ReadAllText(Path.Combine(closeFolder, "deep", "deep-trace.json")));
            Assert(closed.artifacts.Count == 0 && !closed.active && closed.startWindows.Count == 1 && closeFake.calls.Any(c => c.StartsWith("-cancel -instancename ")) && !closeFake.calls.Any(c => c.StartsWith("-stop")),
                "Closing without a pending long frame discards the ring buffer instead of saving an ETL");
            string pendingFolder = Path.Combine(folder, "closed-pending"); Directory.CreateDirectory(pendingFolder);
            var pendingFake = new FakeWpr();
            var pendingTrace = new DeepTrace(pendingFolder, Stopwatch.GetTimestamp(), delegate { }, pendingFake, true);
            pendingTrace.Start(); Assert(Until(() => pendingTrace.Armed, 3000), "Pending-close test starts an active trace");
            pendingTrace.Trigger(new TraceTrigger { frameMs = 90, source = "frame" }); pendingTrace.RequestStop(); Assert(pendingTrace.Wait(5000), "Closing with a pending long frame exits");
            var pendingState = Json.Decode<TraceState>(File.ReadAllText(Path.Combine(pendingFolder, "deep", "deep-trace.json")));
            Assert(pendingState.artifacts.Count == 1 && pendingState.artifacts[0].trigger != null, "A long frame seen just before closing is still saved");
        }
        public static int RunAdmin()
        {
            string resultPath = Path.Combine(Program.Home, "tests", "output", "admin-smoke-result.json");
            Session session = null; bool stopped = false;
            try
            {
                if (!Session.IsAdmin) throw new InvalidOperationException("Administrator approval is required for the live ETW test");
                session = new Session(Path.Combine(Program.Home, "tests", "output", "admin-live"), "");
                session.Start(true, true);
                Assert(Until(() => session.Deep.Armed || session.Deep.Status.Contains("失败"), 60000) && session.Deep.Armed, "Actual WPR memory recording started");
                Thread.Sleep(4000);
                double now = Clock.Seconds(session.Origin);
                session.Deep.Trigger(new TraceTrigger { t = now, observedAt = now, application = "SELF-TEST", frameMs = 80, baselineMs = 8, source = "self-test" });
                string traceFile = Path.Combine(session.Folder, "deep", "trace-01.etl");
                Assert(Until(() => File.Exists(traceFile) && session.Deep.Status.Contains("冷却"), 150000), "Real ETL saved through the automatic-save path");
                string report = session.StopAndReport(); stopped = true;
                var state = Json.Decode<TraceState>(File.ReadAllText(Path.Combine(session.Folder, "deep", "deep-trace.json")));
                Assert(state.error == null && state.artifacts.Count == 1 && state.artifacts[0].bytes > 1024, "Real capture has a complete ETL artifact");
                Assert(session.FrameRows > 0, "PresentMon and WPR recorded concurrently");
                string statusText = File.ReadAllText(Path.Combine(session.Folder, "deep", "trace-01-status.txt"));
                Assert(statusText.Contains("CSwitch") && statusText.Contains("ReadyThread") && statusText.Contains("DPC") && statusText.Contains("DxgKrnl"), "Live recorder confirms scheduling, DPC and graphics providers");
                var check = new WprCommands(Path.Combine(session.Folder, "deep")).Run("-status -instancename " + state.instanceName, 15000);
                Assert(check.output.IndexOf("not recording", StringComparison.OrdinalIgnoreCase) >= 0 || check.code != 0, "Owned WPR instance stopped after save");
                Json.Save(resultPath, new { success = true, report = report, etl = traceFile, frames = session.FrameRows, deep = state });
                Console.WriteLine("Live ETW test passed. " + report); return 0;
            }
            catch (Exception ex)
            {
                Json.Save(resultPath, new { success = false, error = ex.ToString(), folder = session == null ? null : session.Folder });
                Console.Error.WriteLine(ex); return 1;
            }
            finally { if (session != null && !stopped) try { session.StopAndReport(); } catch { } }
        }
    }
}
