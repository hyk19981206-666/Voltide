using System;
using System.Collections.Generic;
using System.IO;

namespace Voltide.Interop
{
    /// <summary>
    /// 极简日志落盘。存在的理由很具体：
    ///
    /// 本机 Low 完整性进程 **不能** 写 %TEMP%（实测 UnauthorizedAccessException），
    /// 也不能写 %LOCALAPPDATA%\Voltide（父目录是 Medium）。原来的 Program.Log 只写
    /// %TEMP%\Voltide-error.txt，于是「Low 下托盘注册失败」这种最该留证据的场景里，
    /// 日志本身也写不进去，故障完全静默。
    ///
    /// 所以按顺序找第一个可写目录：%TEMP% → %LOCALAPPDATA%\Voltide\logs → %USERPROFILE%\AppData\LocalLow\Voltide。
    /// 最后一个目录带 Low 标签，Low 进程可写（实测通过）。写成功一次就记住，不再试探。
    /// </summary>
    internal static class LogSink
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, string> Resolved =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前进程实际可写的日志目录（探测用；返回 null 表示一个都写不进去）。</summary>
        public static string WritableDirectory
        {
            get
            {
                foreach (string dir in Candidates())
                {
                    if (CanWrite(dir)) return dir;
                }
                return null;
            }
        }

        /// <summary>某个日志文件名已经落在哪个完整路径；还没写过则返回 null。</summary>
        public static string PathFor(string fileName)
        {
            lock (Gate)
            {
                string path;
                return Resolved.TryGetValue(fileName, out path) ? path : null;
            }
        }

        /// <summary>追加一行（自动加时间戳）。返回落盘完整路径；全部失败返回 null。</summary>
        public static string Append(string fileName, string text)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + text + Environment.NewLine;

            lock (Gate)
            {
                string cached;
                if (Resolved.TryGetValue(fileName, out cached))
                {
                    if (TryAppend(cached, line)) return cached;
                    Resolved.Remove(fileName);
                }

                foreach (string dir in Candidates())
                {
                    string path = Path.Combine(dir, fileName);
                    if (TryAppend(path, line))
                    {
                        Resolved[fileName] = path;
                        return path;
                    }
                }
            }
            return null;
        }

        /// <summary>候选目录，顺序即优先级。</summary>
        private static IEnumerable<string> Candidates()
        {
            string temp = null;
            try { temp = Path.GetTempPath(); }
            catch { }
            if (!string.IsNullOrEmpty(temp)) yield return temp;

            string local = null;
            try
            {
                local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                     "Voltide", "logs");
            }
            catch { }
            if (!string.IsNullOrEmpty(local)) yield return local;

            string low = null;
            try
            {
                low = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                   "AppData", "LocalLow", "Voltide");
            }
            catch { }
            if (!string.IsNullOrEmpty(low)) yield return low;
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, "Voltide-write-probe.tmp");
                File.WriteAllText(probe, "probe");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryAppend(string path, string line)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(path, line);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
