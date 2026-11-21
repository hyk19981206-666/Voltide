using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Voltide.UI
{
    /// <summary>
    /// Start-with-Windows via the per-user Run key.
    ///
    /// HKCU is used rather than a scheduled task or the Startup folder: no admin
    /// rights, no extra files, and the entry can be removed cleanly when the switch
    /// is turned off.
    ///
    /// 契约（卡 2 固定，不再允许漂移）：
    ///   HKCU\Software\Microsoft\Windows\CurrentVersion\Run
    ///   Voltide = "<R>\Voltide.exe" --background
    /// 其中 R = %LOCALAPPDATA%\Voltide\app，且必须带 --background，
    /// 否则开机自启会把面板弹出来（旧实现正是这么写的，见 G2）。
    /// </summary>
    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Voltide";
        private const string BackgroundArgument = "--background";

        /// <summary>本进程应当写进/被读回的命令行，形如 <c>"<exe>" --background</c>。</summary>
        public static string ExpectedCommand
        {
            get
            {
                try { return "\"" + Application.ExecutablePath + "\" " + BackgroundArgument; }
                catch { return null; }
            }
        }

        /// <summary>
        /// False when the current process is not Voltide.exe — the in-memory build
        /// (run.cmd) runs under powershell.exe, and writing that path would launch a
        /// PowerShell window at every boot instead of the app.
        /// </summary>
        public static bool IsSupported()
        {
            try
            {
                string exe = Application.ExecutablePath;
                return exe.EndsWith("Voltide.exe", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 已启用 = Run 键里存在 Voltide 值，且该值指向**当前 exe**并带 --background。
        /// 旧实现只判 GetValue != null：路径变了、甚至指向已经删掉的目录，也会显示「已开启」。
        /// </summary>
        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;

                    object raw = key.GetValue(ValueName);
                    if (raw == null) return false;

                    string command = raw as string;
                    bool matches = Matches(command);

                    if (!matches)
                    {
                        // 有值但内容不对：视为未启用，并说明为什么不认它。
                        Program.Log("自启: Run 值存在但不匹配当前 exe，按未启用处理。原有值=" +
                                    (command == null ? raw.ToString() : command) +
                                    "；期望=" + ExpectedCommand);
                    }

                    return matches;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Run 键里当前的原始值（没写过返回 null），诊断用。</summary>
        public static string CurrentCommand()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return null;
                    object raw = key.GetValue(ValueName);
                    return raw as string;
                }
            }
            catch
            {
                return null;
            }
        }

        public static bool Set(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return false;

                    if (enabled)
                    {
                        // 带引号：路径可能含空格；必须带 --background，否则登录就弹面板。
                        string command = ExpectedCommand;
                        if (command == null) return false;

                        key.SetValue(ValueName, command);
                        Program.Log("自启: 已写入 " + command);
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                        Program.Log("自启: 已删除 Run 值 " + ValueName);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Program.Log("自启: 写 Run 键失败: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 判定一个 Run 值是否等价于契约。要求：解析出的路径 == 当前 exe（忽略大小写），
        /// 且余下的参数里含 --background（不区分大小写）。无法解析的值一律 false。
        /// </summary>
        private static bool Matches(string command)
        {
            if (string.IsNullOrEmpty(command)) return false;

            string exe;
            try { exe = Application.ExecutablePath; }
            catch { return false; }
            if (string.IsNullOrEmpty(exe)) return false;

            string trimmed = command.Trim();
            string quoted = "\"" + exe + "\"";
            string remainder;

            if (trimmed.StartsWith(quoted, StringComparison.OrdinalIgnoreCase))
            {
                remainder = trimmed.Substring(quoted.Length);
            }
            else if (trimmed.StartsWith(exe, StringComparison.OrdinalIgnoreCase))
            {
                // 没写引号的情况也认，路径本身不含空格时才可能正确。
                remainder = trimmed.Substring(exe.Length);
            }
            else
            {
                return false;
            }

            return remainder.IndexOf(BackgroundArgument, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
