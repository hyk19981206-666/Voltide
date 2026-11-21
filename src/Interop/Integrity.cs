using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Voltide.Interop
{
    /// <summary>
    /// 当前进程的完整性级别（Mandatory Integrity Level）。
    ///
    /// 为什么需要它：本机 <c>D:\workspace</c> 目录带有
    /// <c>Mandatory Label\Low Mandatory Level:(OI)(CI)(NW)</c>，其下编译出来的
    /// <c>Voltide.exe</c> 会以 Low（S-1-16-4096）运行；explorer.exe 是 Medium（S-1-16-8192），
    /// UIPI 不允许 Low 进程向 Medium 进程注册托盘图标（Shell_NotifyIcon NIM_ADD 返回 0、lastError=5）。
    /// 因此程序必须能读到自己的完整性级别，才能给出正确诊断，而不是把「构建成功」当成「托盘可用」。
    ///
    /// 实现照抄 evidence\09\inspection.md §0 的可复用探针：
    /// OpenProcessToken → GetTokenInformation(TokenIntegrityLevel=25) → 解析 SID 末位 RID。
    /// </summary>
    internal static class Integrity
    {
        public const int LowRid = 4096;
        public const int MediumRid = 8192;

        private const uint TokenQuery = 0x0008;
        private const int TokenIntegrityLevel = 25;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
                                                       IntPtr tokenInformation, int tokenInformationLength,
                                                       out int returnLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static bool _probed;
        private static bool _known;
        private static int _rid = -1;
        private static string _sid;
        private static string _error;

        /// <summary>false 表示探测本身失败（不采信任何猜测）。</summary>
        public static bool Known
        {
            get { Probe(); return _known; }
        }

        /// <summary>4096 = Low，8192 = Medium，12288 = High，16384 = System；-1 = 未知。</summary>
        public static int Rid
        {
            get { Probe(); return _rid; }
        }

        public static string SidString
        {
            get { Probe(); return _sid; }
        }

        /// <summary>Low（或更低）→ true。未知时 false：绝不把「探测失败」当成「是 Low」。</summary>
        public static bool IsLow
        {
            get { Probe(); return _known && _rid >= 0 && _rid < MediumRid; }
        }

        public static bool AtLeastMedium
        {
            get { Probe(); return _known && _rid >= MediumRid; }
        }

        public static string LevelName
        {
            get
            {
                Probe();
                if (!_known) return "Unknown";
                if (_rid < 4096) return "Untrusted";
                if (_rid < 8192) return "Low";
                if (_rid < 12288) return "Medium";
                if (_rid < 16384) return "High";
                return "System";
            }
        }

        /// <summary>一行可读摘要，直接进日志。</summary>
        public static string Describe()
        {
            Probe();
            if (!_known)
            {
                return "Unknown (探测失败" + (_error != null ? ": " + _error : "") + ")";
            }
            return LevelName + " (RID=" + _rid + ", " + _sid + ")";
        }

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;

            IntPtr token = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
                {
                    _error = "OpenProcessToken error=" + Marshal.GetLastWin32Error();
                    return;
                }

                int size = 0;
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out size);
                if (size <= 0) size = 64;

                buffer = Marshal.AllocHGlobal(size);
                int needed;
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out needed))
                {
                    _error = "GetTokenInformation error=" + Marshal.GetLastWin32Error();
                    return;
                }

                // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label; }：
                // 第一个指针指向同一块缓冲区里的 SID。
                IntPtr sidPtr = Marshal.ReadIntPtr(buffer);
                if (sidPtr == IntPtr.Zero)
                {
                    _error = "SID 指针为空";
                    return;
                }

                byte subCount = Marshal.ReadByte(sidPtr, 1);
                int total = 8 + 4 * subCount;
                byte[] bytes = new byte[total];
                Marshal.Copy(sidPtr, bytes, 0, total);

                _sid = new SecurityIdentifier(bytes, 0).Value;
                _rid = Marshal.ReadInt32(sidPtr, 8 + 4 * (subCount - 1));
                _known = true;
            }
            catch (Exception ex)
            {
                _error = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }
    }
}
