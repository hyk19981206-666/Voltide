using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Voltide.Interop;

namespace Voltide.UI
{
    /// <summary>一次托盘注册探测的原始结果。字段全部来自实测，不含推断。</summary>
    internal struct TrayProbeResult
    {
        public bool Probed;          // 是否真的调用了 Shell_NotifyIconGetRect
        public string Rule;          // internal-window / form-handle / none
        public IntPtr Hwnd;
        public uint Id;
        public int Hr;               // HRESULT；0 = 命中
        public Rectangle Rect;
        public string Note;
        public bool Registered { get { return Probed && Hr == 0; } }
    }

    /// <summary>
    /// 托盘注册的可观测性（G4）。
    ///
    /// 背景：WinForms 的 <c>NotifyIcon.Visible = true</c> 在 <c>Shell_NotifyIcon(NIM_ADD)</c>
    /// 被拒绝时**既不抛异常也不写日志**，所以 Low 完整性下「托盘没有图标」在程序侧完全无感。
    /// 能读到的唯一客观信号是 <c>Shell_NotifyIconGetRect</c>：图标确实注册了才会返回 S_OK 并给出矩形。
    ///
    /// 实测（evidence\09\probe，2026-11-09）：
    ///   · 任务卡最初的假设 hWnd = 窗体句柄 + uID = 0 **命中不了**，返回 0x80004005；
    ///   · 真正注册用的是 NotifyIcon 内部 <c>NotifyIconNativeWindow</c> 的句柄与内部 <c>id</c> 字段，
    ///     该组合返回 S_OK（Medium，rect=(3428,2064)-(3492,2160)）；Low 下同一组合返回 0x80004005。
    /// 因此这里用只读反射拿内部句柄与 id；反射失败时退回窗体句柄并把失败原因写清楚，绝不伪造成「成功」。
    /// </summary>
    internal static class TrayDiagnostics
    {
        private const string LogFile = "Voltide-tray.log";

        private const int ErrorSuccess = 0;
        private const int ErrorAccessDenied = 5;
        // 0x80004005 = E_FAIL，实测 WinForms 图标用「窗体句柄 + uID」探测时的返回值。
        private const int ErrorHostUnreachable = unchecked((int)0x80004005);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        // 字段顺序即 Win32 布局。cbSize 在 x64 上是 40（Marshal.SizeOf 实测）。
        [StructLayout(LayoutKind.Sequential)]
        private struct NOTIFYICONIDENTIFIER
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public Guid GuidItem;
        }

        [DllImport("shell32.dll", SetLastError = false)]
        private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

        /// <summary>支持的启动入口：自动用户部署后的正式映像。</summary>
        public static string SupportEntryPath
        {
            get
            {
                try
                {
                    return Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Voltide", "app", "Voltide.exe");
                }
                catch
                {
                    return @"%LOCALAPPDATA%\Voltide\app\Voltide.exe";
                }
            }
        }

        /// <summary>当前托盘日志的完整路径（还没写过时为 null）。</summary>
        public static string LogPath
        {
            get { return LogSink.PathFor(LogFile); }
        }

        public static void Log(string message)
        {
            string path = null;
            try { path = LogSink.Append(LogFile, "[托盘] " + message); }
            catch { }

            // LogSink 全线失败时退到错误日志，至少别把异常吞干净。
            if (path == null)
            {
                try { Program.Log("tray: " + message); }
                catch { }
            }
        }

        public static void LogException(string what, Exception ex)
        {
            Log(what + ": " + ex.GetType().Name + ": " + ex.Message);
        }

        /// <summary>
        /// 只读探测托盘图标是否真的注册成功。<paramref name="tray"/> 必须是已经
        /// <c>Visible = true</c> 的那个实例。
        /// </summary>
        public static TrayProbeResult Probe(NotifyIcon tray, IntPtr fallbackHwnd)
        {
            TrayProbeResult result = new TrayProbeResult();
            result.Rule = "none";
            result.Hr = ErrorHostUnreachable;

            IntPtr hwnd = IntPtr.Zero;
            uint id = 0;
            string note = null;

            try
            {
                Type type = typeof(NotifyIcon);
                FieldInfo windowField = type.GetField("window", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo idField = type.GetField("id", BindingFlags.Instance | BindingFlags.NonPublic);

                if (windowField == null || idField == null)
                {
                    note = "反射失败：找不到 NotifyIcon.window / NotifyIcon.id 私有字段（.NET 版本可能不同）";
                }
                else
                {
                    object window = windowField.GetValue(tray);
                    if (window != null)
                    {
                        PropertyInfo handleProp = window.GetType().GetProperty(
                            "Handle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (handleProp != null) hwnd = (IntPtr)handleProp.GetValue(window, null);
                    }

                    if (hwnd != IntPtr.Zero)
                    {
                        id = (uint)Convert.ToInt32(idField.GetValue(tray));
                        result.Rule = "internal-window";
                    }
                    else
                    {
                        note = "反射到 NotifyIcon.window 但句柄为空（图标可能还没创建）";
                    }
                }
            }
            catch (Exception ex)
            {
                note = "反射异常: " + ex.GetType().Name + ": " + ex.Message;
            }

            if (hwnd == IntPtr.Zero && fallbackHwnd != IntPtr.Zero)
            {
                // 兜底路径。实测这个组合命中不了 WinForms 注册的图标（0x80004005），
                // 写进日志是为了让「探测不到」和「确实没注册」区分开。
                hwnd = fallbackHwnd;
                id = 0;
                result.Rule = "form-handle";
                note = (string.IsNullOrEmpty(note) ? "" : note + "; ") +
                       "回退到窗体句柄 + uID=0（实测该组合不命中 WinForms 图标，结果不可作为成功依据）";
            }

            if (hwnd == IntPtr.Zero)
            {
                result.Probed = false;
                result.Note = string.IsNullOrEmpty(note) ? "没有可探测的窗口句柄" : note;
                return result;
            }

            Rectangle rect;
            int hr = GetRect(hwnd, id, out rect);

            result.Probed = true;
            result.Hwnd = hwnd;
            result.Id = id;
            result.Hr = hr;
            result.Note = note;
            if (hr == ErrorSuccess) result.Rect = rect;
            return result;
        }

        private static int GetRect(IntPtr hwnd, uint id, out Rectangle rect)
        {
            rect = Rectangle.Empty;

            NOTIFYICONIDENTIFIER ident = new NOTIFYICONIDENTIFIER();
            ident.cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER));
            ident.hWnd = hwnd;
            ident.uID = id;
            ident.GuidItem = Guid.Empty;

            RECT raw;
            int hr = Shell_NotifyIconGetRect(ref ident, out raw);
            if (hr == ErrorSuccess)
            {
                rect = Rectangle.FromLTRB(raw.Left, raw.Top, raw.Right, raw.Bottom);
            }
            return hr;
        }

        /// <summary>把探测结果和判定写进日志。判定只用实测 + RID，不猜测原因。</summary>
        public static void LogProbe(TrayProbeResult result)
        {
            Log("---- 托盘注册探测 ----");
            Log("进程完整性: " + Integrity.Describe());
            Log("探测规则  : " + result.Rule + (result.Probed ? "" : "（未发起）"));

            if (result.Probed)
            {
                Log("GetRect   : hwnd=0x" + result.Hwnd.ToInt64().ToString("X") +
                    " uID=" + result.Id +
                    " hr=0x" + result.Hr.ToString("X8") +
                    (result.Hr == ErrorSuccess ? " rect=" + result.Rect : " rect=<无>"));
            }

            if (!string.IsNullOrEmpty(result.Note)) Log("备注      : " + result.Note);

            if (result.Registered)
            {
                Log("判定      : 已注册（Shell_NotifyIconGetRect 命中，图标确实在通知区域里）");
                return;
            }

            Log("判定      : 未注册 / 无法确认");

            if (Integrity.IsLow)
            {
                Log("原因      : 当前进程为 Low 完整性（S-1-16-4096），UIPI 禁止向 Medium 的 explorer 注册托盘图标。");
                Log("处理      : 请从 " + SupportEntryPath + " 启动（或运行 Voltide.cmd 自动部署）。");
            }
            else if (result.Probed && result.Hr == unchecked((int)0x80070005) /* E_ACCESSDENIED */)
            {
                Log("原因      : GetRect 返回 E_ACCESSDENIED，但进程完整性不低于 Medium，原因不明（不猜测）。");
            }
            else if (result.Probed && result.Hr != ErrorSuccess)
            {
                Log("原因      : GetRect 返回非 S_OK，进程完整性不低于 Medium，原因不明（不猜测）。");
            }
        }

        /// <summary>启动、退出等生命周期事实，独立于探测结果写一行。</summary>
        public static void LogLifecycle(string message)
        {
            Log(message);
        }

        public static string DescribeAccessDenied(int lastError)
        {
            return lastError == ErrorAccessDenied ? "（lastError=5，访问被拒绝）" : "（lastError=" + lastError + "）";
        }
    }
}
