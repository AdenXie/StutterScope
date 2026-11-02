using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace StutterScope
{
    public static class Program
    {
        public static string Home { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        public static string Root { get { return Path.Combine(Home, "recordings"); } }
        static volatile bool requestedStop;
        static string controllerFolder;
        delegate bool Handler(uint kind);
        static Handler closeHandler;
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(Handler handler, bool add);
        static string Quote(string value) { return "\"" + value.Replace("\"", "") + "\""; }
        static string Option(string[] args, string key, string fallback)
        {
            int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }
        [STAThread]
        public static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                if (args.Contains("--self-test")) return SelfTest.Run();
                if (args.Contains("--admin-self-test")) return SelfTest.RunAdmin();
                if (args.Contains("--report")) { Console.WriteLine(Report.Build(Option(args, "--report", ""))); return 0; }
                if (args.Contains("--latest")) { OpenLatest(Option(args, "--root", Root)); return 0; }
                if (args.Contains("--worker")) return RunWorker(args);
                if (args.Contains("--capture")) return Capture(args);
                if (!Session.IsAdmin && !args.Contains("--console-no-elevate"))
                {
                    try { Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, String.Join(" ", args.Select(Quote))) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Home }); return 0; }
                    catch (System.ComponentModel.Win32Exception ex) { Console.WriteLine("未启动：管理员授权未完成。" + ex.Message); return 1; }
                }
                using (var mutex = new Mutex(false, "Local\\StutterScope.Controller"))
                {
                    bool entered; try { entered = mutex.WaitOne(0); } catch (AbandonedMutexException) { entered = true; }
                    if (!entered) { Console.WriteLine("已有一个记录窗口正在运行。请关闭原窗口后再启动。"); Thread.Sleep(3000); return 1; }
                    try { return Controller(Option(args, "--root", Root), args.Contains("--deep")); } finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(Home, "error.log"), DateTime.Now.ToString("o") + " " + ex + Environment.NewLine, Encoding.UTF8); } catch { }
                Console.Error.WriteLine("失败：" + ex.Message + "。详情见 error.log。"); return 1;
            }
        }
        static int Controller(string root, bool deep)
        {
            Console.Title = (deep ? "游戏卡顿深度诊断" : "游戏卡顿监控") + " — 关闭此窗口停止记录";
            var session = new Session(root, ""); // Reserves a unique directory; collection happens only in the detached worker.
            controllerFolder = session.Folder;
            closeHandler = delegate(uint kind) { if (kind == 0 || kind == 1 || kind == 2 || kind == 5 || kind == 6) RequestStop(); return false; };
            SetConsoleCtrlHandler(closeHandler, true);
            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; RequestStop(); };
            Console.WriteLine("游戏卡顿监控  /  StutterScope");
            if (deep) Console.WriteLine("深度诊断：256 MB循环缓冲；前台明显长帧自动保存ETL，最多3份。整理期间可能有额外开销。");
            Console.WriteLine("现在可以启动游戏。关闭此窗口停止记录，也可按 Enter 停止并等待报告。");
            Console.WriteLine("报告保存到：" + session.Folder);
            Console.WriteLine("停止后后台会整理报告；深度跟踪可能需数十秒。使用“查看最新报告.cmd”打开报告。");
            Console.WriteLine("上限：2小时 / 512 MiB；不设置全局快捷键。\n");
            using (var own = Process.GetCurrentProcess())
            using (var worker = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                "--worker --parent " + own.Id + " --parent-start " + own.StartTime.ToUniversalTime().Ticks + " --folder " + Quote(session.Folder) + (deep ? " --deep" : "")) {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Home }))
            {
                Json.Save(Path.Combine(session.Folder, "worker.json"), new { pid = worker.Id, parentPid = own.Id });
                int lines = 0;
                while (!worker.WaitForExit(1000))
                {
                    if (!requestedStop && !Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Enter) RequestStop();
                    string statusPath = Path.Combine(session.Folder, "status.txt");
                    try
                    {
                        if (File.Exists(statusPath))
                        {
                            string status = File.ReadAllText(statusPath, Encoding.UTF8).Trim();
                            if (lines++ % 5 == 0) Console.WriteLine(status);
                        }
                    }
                    catch (IOException) { }
                }
                Console.WriteLine("\n记录已停止。" + (File.Exists(Path.Combine(session.Folder, "report.html")) ? "报告已保存。" : "请检查 error.log 或 monitor.log。"));
                if (worker.ExitCode != 0) { Console.WriteLine("另一轮可能仍在保存报告。等待几秒后可重试启动。"); Thread.Sleep(4000); }
                return worker.ExitCode;
            }
        }
        static void RequestStop()
        {
            requestedStop = true;
            try { if (controllerFolder != null) File.WriteAllText(Path.Combine(controllerFolder, "stop.request"), "stop"); } catch { }
        }
        static int RunWorker(string[] args)
        {
            using (var mutex = new Mutex(false, "Local\\StutterScope.Worker"))
            {
                bool entered; try { entered = mutex.WaitOne(0); } catch (AbandonedMutexException) { entered = true; }
                if (!entered) return 2;
                try
                {
                    int parentId = Int32.Parse(Option(args, "--parent", "0"), CultureInfo.InvariantCulture);
                    long parentStart = Int64.Parse(Option(args, "--parent-start", "0"), CultureInfo.InvariantCulture);
                    string folder = Option(args, "--folder", "");
                    var session = new Session(folder, "", true);
                    Process parent = null;
                    try { parent = Process.GetProcessById(parentId); if (parent.StartTime.ToUniversalTime().Ticks != parentStart) { parent.Dispose(); parent = null; } }
                    catch (ArgumentException) { }
                    session.Start(true, args.Contains("--deep"));
                    try
                    {
                        while (session.StopReason == null && parent != null && !parent.WaitForExit(1000) && !File.Exists(Path.Combine(folder, "stop.request")))
                        {
                            string status = Status(session);
                            try { File.WriteAllText(Path.Combine(folder, "status.txt"), status, Encoding.UTF8); } catch (IOException) { }
                        }
                    }
                    finally { if (parent != null) parent.Dispose(); }
                    string report = session.StopAndReport();
                    Json.Save(Path.Combine(Path.GetDirectoryName(folder), "latest.json"), new { report = report });
                    return 0;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        static string Status(Session session)
        {
            var sample = session.Latest;
            string cpu = sample != null && sample.cpu.HasValue ? sample.cpu.Value.ToString("F0") + "%" : "等待采样";
            string self = sample != null && sample.monitorCpu.HasValue ? sample.monitorCpu.Value.ToString("F1") + "%" : "--";
            return String.Format("已记录 {0:hh\\:mm\\:ss} | 帧数 {1:N0} | CPU {2} | 监控CPU {3} | {4}", TimeSpan.FromSeconds(Clock.Seconds(session.Origin)), Interlocked.Read(ref session.FrameRows), cpu, self, session.FrameStatus)
                + (session.Deep == null ? "" : " | " + session.Deep.Status);
        }
        static int Capture(string[] args)
        {
            double seconds = Double.Parse(Option(args, "--capture", "10"), CultureInfo.InvariantCulture);
            if (seconds < 1 || seconds > 7200) throw new ArgumentException("--capture 应为1至7200秒");
            string filter = Option(args, "--process", "");
            if (filter.Any(c => c == '"' || c == '\r' || c == '\n')) throw new ArgumentException("进程名格式无效");
            var session = new Session(Option(args, "--root", Root), filter);
            session.Start(!args.Contains("--system-only"), args.Contains("--deep"));
            Console.WriteLine(session.Folder);
            double deadline = Clock.Seconds(session.Origin) + seconds;
            while (Clock.Seconds(session.Origin) < deadline && session.StopReason == null) Thread.Sleep(200);
            string report = session.StopAndReport();
            Json.Save(Path.Combine(Path.GetDirectoryName(session.Folder), "latest.json"), new { report = report });
            Console.WriteLine(report); return 0;
        }
        static void OpenLatest(string root)
        {
            if (Directory.Exists(root))
            {
                var newest = new DirectoryInfo(root).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).FirstOrDefault();
                if (newest != null && !File.Exists(Path.Combine(newest.FullName, "report.html")))
                {
                    Console.WriteLine("最近一轮还在记录或整理，报告尚未生成。关闭记录窗口后，深度跟踪可能需要数十秒；稍后再试。若长时间未完成，请检查该轮deep目录日志。");
                    Thread.Sleep(4000); return;
                }
            }
            string index = Path.Combine(root, "latest.json"), report = null;
            if (File.Exists(index))
            {
                var latest = Json.Decode<System.Collections.Generic.Dictionary<string, string>>(File.ReadAllText(index)); latest.TryGetValue("report", out report);
            }
            if (report == null || !File.Exists(report))
            {
                if (Directory.Exists(root)) report = Directory.GetDirectories(root).Select(p => Path.Combine(p, "report.html")).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }
            if (report == null) { Console.WriteLine("还没有完成的报告。关闭记录窗口后请等待几秒再试。"); Thread.Sleep(4000); return; }
            Process.Start(new ProcessStartInfo(report) { UseShellExecute = true });
        }
    }
}
