using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Voltide.Core;
using Voltide.Interop;
using Voltide.Tests;
using Voltide.UI;

namespace Voltide
{
    public static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// 第二个实例唤回面板用的窗口消息（WM_APP + 123）。
        /// 定义在这里、由 <see cref="MainForm"/> 的 WndProc 处理，保证「发」与「收」是同一个常量，
        /// 不再两边各写一个魔数。
        /// </summary>
        public const int PanelMessage = 0x8000 + 123;

        /// <summary>
        /// Entry point usable from an in-memory compiled assembly
        /// (Add-Type ... then [Voltide.Program]::Run(@('--show'))).
        /// </summary>
        public static void Run(string[] args)
        {
            Main(args != null ? args : new string[0]);
        }

        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                if (args != null && args.Length > 0 && args[0] == "--test-low-battery")
                {
                    // 离线自检入口：只跑低电判定/去重/汇总的生产代码，喂构造出来的快照序列。
                    // 这一支不建 DeviceManager、不枚举 HID、不建托盘、不进消息循环、不碰
                    // 单实例 mutex，也不写任何日志或状态文件 —— 全部断言通过退出 0，
                    // 任一断言失败或测试自身异常退出非 0。
                    Environment.ExitCode = LowBatterySelfTest.Run(HasFlag(args, "--json"));
                    return;
                }

                if (args != null && args.Length > 0 && args[0] == "--test-dualsense")
                {
                    // DualSense / DS5Dongle 协议与发现逻辑的离线自检，不碰真实 HID/托盘。
                    Environment.ExitCode = DualSenseSelfTest.Run(HasFlag(args, "--json"));
                    return;
                }

                if (args != null && args.Length > 0 && args[0] == "--test")
                {
                    // --test is the read-only production probe: it builds the real
                    // DeviceManager, refreshes it once and prints what it found.
                    // --json adds one machine-readable line for the verification script.
                    RunTest(HasFlag(args, "--json"));
                    return;
                }

                // 命令行契约（细节见 evidence\09\implementation.md）：
                //   无参数       → 后台常驻，不显示面板（行为变更：旧版本无参数必弹面板）
                //   --show       → 启动后显示主界面（手动启动入口用）
                //   --background → 显式声明「后台启动」，HKCU Run 键写的就是这个
                // 旧实现里 autoShow 初值为 true，导致「开机自启」等同于「登录就弹窗」，
                // 而净化后的契约要求后台启动必须有明确的、不弹面板的入口。
                bool background = HasFlag(args, "--background");
                bool forceShow = HasFlag(args, "--show");
                bool autoShow = forceShow;

                LogStartup(args, background, forceShow, autoShow);

                bool createdNew;
                using (Mutex mutex = new Mutex(true, "Voltide-SingleInstance", out createdNew))
                {
                    if (!createdNew)
                    {
                        // 第二个实例：请已在运行的实例显示面板，然后自己退出。
                        // 注意：mutex 名不绑定路径，R\Voltide.exe 与 dist\Voltide.exe
                        // 在同一登录会话里互斥，只能活一个（已知限制，见 implementation.md）。
                        TrayLog("第二个实例：已存在运行中的 Voltide，请求唤回面板后退出");
                        SignalShowPanel();
                        return;
                    }

                    SetProcessDPIAware();
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);

                    Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                    {
                        Log("UI thread: " + e.Exception);
                    };

                    AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                    {
                        Log("Domain: " + e.ExceptionObject);
                    };

                    using (TrayApp app = new TrayApp())
                    {
                        // 托盘注册结果在这里落日志：这是「托盘到底有没有出现」的唯一程序侧事实，
                        // 不能用 NotifyIcon.Visible == true 代替。
                        if (app.TrayRegistered) TrayLog("托盘注册结果: 成功");
                        else TrayLog("托盘注册结果: 失败");

                        // Low 完整性下托盘必然不可用：给一次明确提示（--background 启动时不打扰）。
                        if (!app.TrayRegistered && !background) NotifyTrayUnavailable();

                        if (autoShow) app.ShowPanel();

                        TrayLog("进入消息循环（autoShow=" + autoShow + "）");
                        Application.Run();
                        TrayLog("消息循环结束");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Main: " + ex);
                throw;
            }
        }

        /// <summary>
        /// 通用错误日志，落在 %TEMP%\Voltide-error.txt。
        /// 经 <see cref="LogSink"/> 落盘：Low 完整性进程写不了 %TEMP%（实测访问被拒绝），
        /// 会依次退到 %LOCALAPPDATA%\Voltide\logs 与 %USERPROFILE%\AppData\LocalLow\Voltide。
        /// </summary>
        internal static void Log(string msg)
        {
            try { LogSink.Append("Voltide-error.txt", msg); }
            catch
            {
            }
        }

        /// <summary>托盘/启动诊断日志，落在 Voltide-tray.log（同一套可写目录回退）。</summary>
        internal static void TrayLog(string msg)
        {
            TrayDiagnostics.Log(msg);
        }

        /// <summary>
        /// 启动事实一次性写进日志：exe、pid、完整性、参数、日志路径。
        /// 这一行是「程序到底以什么身份、从哪条路径起来」的客观凭据。
        /// </summary>
        private static void LogStartup(string[] args, bool background, bool forceShow, bool autoShow)
        {
            StringBuilder cmdline = new StringBuilder();
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (cmdline.Length > 0) cmdline.Append(' ');
                    cmdline.Append(args[i]);
                }
            }

            TrayLog("================ 启动 ================");
            TrayLog("pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + "  exe=" + ExePath());
            TrayLog("完整性=" + Integrity.Describe());
            TrayLog("参数=[" + cmdline + "]  --background=" + background +
                    "  --show=" + forceShow + "  autoShow=" + autoShow);

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] != "--background" && args[i] != "--show")
                    {
                        TrayLog("未知参数已忽略: " + args[i]);
                    }
                }
            }

            if (Integrity.IsLow)
            {
                TrayLog("诊断: 当前进程是 Low 完整性。UIPI 不允许 Low 进程向 Medium 的 explorer 注册托盘图标，");
                TrayLog("诊断: 所以这个副本的托盘图标不会出现，改代码/重建/重启程序都不会改变这一点。");
                TrayLog("诊断: 支持的启动入口 = " + TrayDiagnostics.SupportEntryPath + "（由 deploy.ps1 自动部署，无需手工搬文件）");
            }
            else if (Integrity.AtLeastMedium)
            {
                TrayLog("完整性 >= Medium：具备注册托盘图标的权限前提（是否成功仍以 GetRect 探测结果为准）。");
            }
        }

        /// <summary>
        /// 低权限提示的正文。单独抽出来是为了让自检程序能直接断言文案
        /// （弹窗本身需要在单实例占用下才有机会出现，见 implementation.md）。
        /// </summary>
        internal static string TrayUnavailableText()
        {
            return "托盘图标注册失败：当前进程以「低完整性」（Low, S-1-16-4096）运行。" + Environment.NewLine +
                   Environment.NewLine +
                   "Windows 的 UIPI 不允许低完整性进程向资源管理器（Medium）注册托盘图标，" +
                   "因此关掉面板后没有图标可以点回来。这不是构建问题。" + Environment.NewLine +
                   Environment.NewLine +
                   "请改用自动用户部署后的入口启动：" + Environment.NewLine +
                   TrayDiagnostics.SupportEntryPath + Environment.NewLine +
                   Environment.NewLine +
                   "（首次部署运行 work\\Voltide\\Voltide.cmd 或 Install-Voltide.ps1，无需手工复制文件）" + Environment.NewLine +
                   Environment.NewLine +
                   "诊断日志：" + (TrayDiagnostics.LogPath ?? "%TEMP%\\Voltide-tray.log");
        }

        /// <summary>
        /// 托盘注册失败时给出一次明确提示。只在「Low 完整性」这一条已证实的原因下弹窗，
        /// 其它原因只写日志——不猜、不把未知原因说成已知原因。
        /// </summary>
        private static void NotifyTrayUnavailable()
        {
            if (!Integrity.IsLow)
            {
                Log("托盘不可用，但进程完整性不是 Low：原因未证实，仅记录，不弹窗。");
                return;
            }

            string text = TrayUnavailableText();

            Log("Low 完整性提示已弹出：托盘不可用，指向 " + TrayDiagnostics.SupportEntryPath);

            try { MessageBox.Show(text, "Voltide", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            catch (Exception ex) { Log("提示弹窗失败: " + ex); }
        }

        /// <summary>
        /// 请求已在运行的实例显示面板：找到标题为 "Voltide" 的顶层窗口，向它投递 WM_APP+123。
        /// MainForm.WndProc 收到后调用 ShowPanel()。
        ///
        /// 这里刻意只尝试一次：失败就写日志退出，不循环重启、不弹窗冒充修复。
        /// </summary>
        private static void SignalShowPanel()
        {
            try
            {
                IntPtr hwnd = FindWindow(null, "Voltide");
                if (hwnd == IntPtr.Zero)
                {
                    TrayLog("唤回失败: FindWindow(null, \"Voltide\") 未找到窗口" +
                            TrayDiagnostics.DescribeAccessDenied(Marshal.GetLastWin32Error()) +
                            "；只尝试一次，不循环启动。");
                    return;
                }

                if (!PostMessage(hwnd, PanelMessage, IntPtr.Zero, IntPtr.Zero))
                {
                    TrayLog("唤回失败: PostMessage(0x" + PanelMessage.ToString("X") + ") 失败" +
                            ", hwnd=0x" + hwnd.ToInt64().ToString("X") +
                            TrayDiagnostics.DescribeAccessDenied(Marshal.GetLastWin32Error()));
                    return;
                }

                TrayLog("唤回成功: PostMessage(0x" + PanelMessage.ToString("X") + ") -> hwnd=0x" +
                        hwnd.ToInt64().ToString("X"));
            }
            catch (Exception ex)
            {
                TrayLog("唤回异常: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool HasFlag(string[] args, string flag)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == flag) return true;
            }
            return false;
        }

        private static void RunTest(bool json)
        {
            DeviceManager m = new DeviceManager();
            m.Refresh();

            List<DeviceSnapshot> snaps = m.Snapshot();
            List<string> providers = m.ProviderNames();
            List<string> conflicts = m.ClaimDiagnostics();

            if (json)
            {
                Console.WriteLine(ToJson(m, snaps, providers, conflicts));
                return;
            }

            Console.WriteLine("devices: " + snaps.Count);
            Console.WriteLine("providers: " + string.Join(", ", providers.ToArray()));
            Console.WriteLine("devices.json: " + DeviceOverrides.SourcePath +
                              (DeviceOverrides.LoadError != null ? " (" + DeviceOverrides.LoadError + ")" : "") +
                              " entries=" + DeviceOverrides.Count);
            if (conflicts.Count > 0) Console.WriteLine("claim conflicts: " + string.Join("; ", conflicts.ToArray()));

            foreach (DeviceSnapshot s in snaps)
            {
                Console.WriteLine("----------------------------------------");
                Console.WriteLine("name    : " + s.Info.Name);
                Console.WriteLine("brand   : " + s.Info.Brand);
                Console.WriteLine("model   : " + s.Info.Model);
                Console.WriteLine("provider: " + s.Info.ProviderName);
                Console.WriteLine("vid/pid : " + s.Info.VendorId.ToString("X4") + ":" + s.Info.ProductId.ToString("X4"));
                Console.WriteLine("container: " + s.Info.ContainerId);
                Console.WriteLine("physical: " + DeviceIdentity.PhysicalKey(s.Info));
                Console.WriteLine("path    : " + s.Info.HidPath);
                Console.WriteLine("online  : " + s.Reading.Online);
                Console.WriteLine("percent : " + (s.Reading.Percent.HasValue ? s.Reading.Percent.Value.ToString() + "%" : "null"));
                Console.WriteLine("charging: " + s.Reading.Charging);
                Console.WriteLine("sleeping: " + s.Reading.Sleeping);
                Console.WriteLine("detail  : " + s.Reading.Detail);
                Console.WriteLine("raw     : " + s.Reading.Raw);
                Console.WriteLine("error   : " + s.Reading.Error);
            }
        }

        /// <summary>
        /// One JSON document describing exactly what the production refresh produced.
        /// Hand-written because the console probe must stay dependency free.
        /// </summary>
        private static string ToJson(DeviceManager manager, List<DeviceSnapshot> snaps,
                                     List<string> providers, List<string> conflicts)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"mode\":\"test\",");
            sb.Append("\"generatedAtLocal\":\"").Append(Escape(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))).Append("\",");
            sb.Append("\"exe\":\"").Append(Escape(ExePath())).Append("\",");
            sb.Append("\"overridesFile\":\"").Append(Escape(DeviceOverrides.SourcePath)).Append("\",");
            sb.Append("\"overridesLoaded\":").Append(DeviceOverrides.LoadError == null ? "true" : "false").Append(',');
            sb.Append("\"overridesError\":");
            if (DeviceOverrides.LoadError == null) sb.Append("null");
            else sb.Append('"').Append(Escape(DeviceOverrides.LoadError)).Append('"');
            sb.Append(",\"overridesCount\":").Append(DeviceOverrides.Count.ToString(Invariant));

            sb.Append(",\"providers\":[");
            for (int i = 0; i < providers.Count; i++)
            {
                if (i != 0) sb.Append(',');
                sb.Append('"').Append(Escape(providers[i])).Append('"');
            }
            sb.Append(']');

            sb.Append(",\"claimDiagnostics\":[");
            for (int i = 0; i < conflicts.Count; i++)
            {
                if (i != 0) sb.Append(',');
                sb.Append('"').Append(Escape(conflicts[i])).Append('"');
            }
            sb.Append(']');

            sb.Append(",\"devices\":[");
            for (int i = 0; i < snaps.Count; i++)
            {
                if (i != 0) sb.Append(',');
                AppendDevice(sb, snaps[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendDevice(StringBuilder sb, DeviceSnapshot snap)
        {
            DeviceInfo info = snap.Info;
            DeviceReading r = snap.Reading != null ? snap.Reading : new DeviceReading();

            sb.Append('{');
            sb.Append("\"name\":\"").Append(Escape(info.Name)).Append("\",");
            sb.Append("\"brand\":\"").Append(Escape(info.Brand)).Append("\",");
            sb.Append("\"model\":\"").Append(Escape(info.Model)).Append("\",");
            sb.Append("\"shortModel\":\"").Append(Escape(info.ShortModel)).Append("\",");
            sb.Append("\"kind\":\"").Append(KindName(info.Kind)).Append("\",");
            sb.Append("\"provider\":\"").Append(Escape(info.ProviderName)).Append("\",");
            sb.Append("\"vid\":\"").Append(info.VendorId.ToString("X4")).Append("\",");
            sb.Append("\"pid\":\"").Append(info.ProductId.ToString("X4")).Append("\",");
            sb.Append("\"id\":\"").Append(Escape(info.Id)).Append("\",");
            sb.Append("\"containerId\":\"").Append(Escape(info.ContainerId)).Append("\",");
            sb.Append("\"physicalKey\":\"").Append(Escape(DeviceIdentity.PhysicalKey(info))).Append("\",");
            sb.Append("\"hidPath\":\"").Append(Escape(info.HidPath)).Append("\",");

            sb.Append("\"online\":").Append(r.Online ? "true" : "false").Append(',');
            sb.Append("\"percent\":");
            if (r.Percent.HasValue) sb.Append(r.Percent.Value.ToString(Invariant));
            else sb.Append("null");
            sb.Append(",\"level\":\"").Append(r.Level.ToString()).Append("\",");
            sb.Append("\"charging\":").Append(r.Charging ? "true" : "false").Append(',');
            sb.Append("\"chargingKnown\":").Append(r.ChargingKnown ? "true" : "false").Append(',');
            sb.Append("\"sleeping\":").Append(r.Sleeping ? "true" : "false").Append(',');
            sb.Append("\"detail\":\"").Append(Escape(r.Detail)).Append("\",");
            sb.Append("\"raw\":\"").Append(Escape(r.Raw)).Append("\",");
            sb.Append("\"error\":\"").Append(Escape(r.Error)).Append("\",");
            sb.Append("\"updatedAtLocal\":");
            if (r.UpdatedAt == default(DateTime)) sb.Append("null");
            else sb.Append('"').Append(r.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append('"');
            sb.Append('}');
        }

        private static readonly System.Globalization.CultureInfo Invariant =
            System.Globalization.CultureInfo.InvariantCulture;

        private static string KindName(DeviceKind kind)
        {
            switch (kind)
            {
                case DeviceKind.Mouse: return "Mouse";
                case DeviceKind.Keyboard: return "Keyboard";
                case DeviceKind.Headset: return "Headset";
                case DeviceKind.Other: return "Other";
                default: return "Unknown";
            }
        }

        private static string ExePath()
        {
            try
            {
                return System.Reflection.Assembly.GetEntryAssembly().Location;
            }
            catch
            {
                return "";
            }
        }

        private static string Escape(string value)
        {
            if (value == null) return "";
            StringBuilder sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
