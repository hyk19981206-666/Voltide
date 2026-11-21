using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using Voltide.Core;

namespace Voltide.UI
{
    /// <summary>提交一条托盘气泡（生产实现绑定 NotifyIcon.ShowBalloonTip）。</summary>
    public delegate void LowBatterySubmitHandler(string title, string text);

    /// <summary>托盘当前是否可用（生产实现绑托盘注册探测结果）。</summary>
    public delegate bool LowBatteryAvailabilityHandler();

    /// <summary>日志落盘（生产实现绑 Program.TrayLog）。</summary>
    public delegate void LowBatteryLogHandler(string message);

    /// <summary>一轮低电处理的客观结果：资格事件数 E、提交调用数 B、正常返回数 S。</summary>
    public class LowBatteryCycleResult
    {
        /// <summary>E —— 本轮新产生的低电资格事件数。</summary>
        public int Events;

        /// <summary>B —— 本轮实际调用提交委托的次数（0 或 1）。</summary>
        public int SubmitCalls;

        /// <summary>S —— 提交委托正常返回的次数（抛异常则不计）。</summary>
        public int SubmitSuccesses;

        /// <summary>托盘不可用导致跳过提交。</summary>
        public bool SkippedUnavailable;

        /// <summary>提交委托抛异常（已捕获）。</summary>
        public bool SubmitFailed;

        /// <summary>本轮气泡标题（没有提交时也为 null）。</summary>
        public string Title;

        /// <summary>本轮气泡正文（没有提交时也为 null）。</summary>
        public string Text;
    }

    /// <summary>
    /// 低电通知的生产协调路径：调状态机拿资格事件 → 生成一条汇总文案 → 最多提交一条气泡。
    ///
    /// 边界说明（也是离线验证能替换的全部内容）：
    ///   * 提交委托 —— 生产是 _tray.ShowBalloonTip，测试换成记录型委托；
    ///   * 可用性委托 —— 生产读托盘注册探测结果，测试可固定成 false 模拟「托盘不可用」；
    ///   * 日志委托 —— 生产写 Voltide-tray.log，测试可丢弃。
    /// 判定与去重逻辑本身不在这里，全部在 LowBatteryMonitor 里，所以测试替换委托之后走的
    /// 仍是同一套生产代码。
    ///
    /// 「提交成功」不等于「用户看到了」：这里只记提交返回，不做任何可见性推断，也不把
    /// 提交结果写进任何持久状态。
    ///
    /// 线程约定：只在 UI 线程串行调用。
    /// </summary>
    public class LowBatteryNotifier
    {
        /// <summary>气泡标题（定稿口径）。</summary>
        public const string BalloonTitle = "Voltide 电量提醒";

        /// <summary>气泡持续时间，沿用卡片口径。</summary>
        public const int BalloonTimeoutMs = 10000;

        /// <summary>单个设备名最多显示的字符数。</summary>
        public const int MaxNameChars = 32;

        /// <summary>汇总时最多逐个列出的设备数。</summary>
        public const int MaxListedDevices = 3;

        /// <summary>正文最大字符数。</summary>
        public const int MaxTextChars = 240;

        private readonly LowBatteryMonitor _monitor;
        private readonly LowBatterySubmitHandler _submit;
        private readonly LowBatteryAvailabilityHandler _available;
        private readonly LowBatteryLogHandler _log;

        /// <summary>
        /// 可注入边界版本：提交、可用性、日志全由调用方提供。
        /// </summary>
        public LowBatteryNotifier(LowBatterySubmitHandler submit,
                                  LowBatteryAvailabilityHandler available,
                                  LowBatteryLogHandler log)
        {
            _monitor = new LowBatteryMonitor();
            _submit = submit;
            _available = available;
            _log = log;
        }

        /// <summary>
        /// 生产版本：气泡提交绑到现有的 <see cref="NotifyIcon"/>。
        /// </summary>
        public LowBatteryNotifier(NotifyIcon tray,
                                  LowBatteryAvailabilityHandler available,
                                  LowBatteryLogHandler log)
            : this(BuildSubmit(tray), available, log)
        {
        }

        /// <summary>当前已提醒的物理键数量（诊断用）。</summary>
        public int MarkedCount
        {
            get { return _monitor.MarkedCount; }
        }

        /// <summary>
        /// 处理一轮快照。永不抛出：任何异常都退化成「本轮没有气泡」，不让托盘校准/面板
        /// 刷新被通知路径连累。
        /// </summary>
        public LowBatteryCycleResult OnSnapshot(List<DeviceSnapshot> snapshots)
        {
            LowBatteryCycleResult result = new LowBatteryCycleResult();

            List<LowBatteryEvent> events;
            try
            {
                events = _monitor.Evaluate(snapshots);
            }
            catch (Exception ex)
            {
                Log("低电判定异常（本轮不提醒）: " + ex.GetType().Name + ": " + ex.Message);
                return result;
            }

            if (events == null || events.Count == 0) return result;

            result.Events = events.Count;
            result.Title = BalloonTitle;
            result.Text = Compose(events);

            bool available = false;
            try
            {
                available = _available != null && _available();
            }
            catch (Exception ex)
            {
                Log("低电提醒: 可用性探测异常，按不可用处理: " + ex.GetType().Name + ": " + ex.Message);
                available = false;
            }

            if (!available)
            {
                // 资格事件已经记进内存状态机；本周期不提交、不重试、不补发。
                result.SkippedUnavailable = true;
                Log("低电提醒: 托盘不可用，跳过提交（本轮资格事件 " +
                    result.Events.ToString() + " 个，不重试）");
                return result;
            }

            result.SubmitCalls = 1;
            try
            {
                _submit(result.Title, result.Text);
                result.SubmitSuccesses = 1;
                Log("低电提醒: 已提交 1 条气泡（资格事件 " + result.Events.ToString() +
                    " 个，正文 " + result.Text.Length.ToString() + " 字符）");
            }
            catch (Exception ex)
            {
                // 提交失败只记录：不重试、不补发，也不清掉已提醒标记（否则会立刻连环重试）。
                result.SubmitFailed = true;
                Log("低电提醒: 提交气泡异常（已捕获，不重试）: " + ex.GetType().Name + ": " + ex.Message);
            }

            return result;
        }

        // ---------------------------------------------------------------- 文案

        /// <summary>
        /// 把本轮全部资格事件合成一条正文。单台写名称+百分比，多台写总数+列表。
        /// </summary>
        public static string Compose(List<LowBatteryEvent> events)
        {
            if (events == null || events.Count == 0) return "";

            StringBuilder sb = new StringBuilder();

            if (events.Count == 1)
            {
                sb.Append(DisplayName(events[0].Info));
                sb.Append(" 电量仅剩 ");
                sb.Append(events[0].Percent.ToString());
                sb.Append("%，请尽快充电。");
            }
            else
            {
                sb.Append("共 ");
                sb.Append(events.Count.ToString());
                sb.Append(" 台设备电量偏低：");

                int listed = events.Count < MaxListedDevices ? events.Count : MaxListedDevices;
                for (int i = 0; i < listed; i++)
                {
                    if (i != 0) sb.Append('、');
                    sb.Append(DisplayName(events[i].Info));
                    sb.Append(' ');
                    sb.Append(events[i].Percent.ToString());
                    sb.Append('%');
                }

                if (events.Count > listed)
                {
                    // 余下的用「等共 N 台」汇总：N 是总数，不是被省略的台数。
                    sb.Append(" 等共 ");
                    sb.Append(events.Count.ToString());
                    sb.Append(" 台");
                }

                sb.Append("。请尽快充电。");
            }

            return ClampText(sb.ToString());
        }

        /// <summary>
        /// 显示名称回退链：Name → ShortModel → Model → Brand → 未命名设备。
        /// 名称里的换行/制表/控制字符压成单个空格：气泡只有一行标题+一段正文，名字里带换行会
        /// 把布局撑坏；引号保留（JSON 由序列化侧转义）。
        /// </summary>
        public static string DisplayName(DeviceInfo info)
        {
            if (info == null) return "未命名设备";

            string name = CleanName(info.Name);
            if (name.Length == 0) name = CleanName(info.ShortModel);
            if (name.Length == 0) name = CleanName(info.Model);
            if (name.Length == 0) name = CleanName(info.Brand);
            if (name.Length == 0) return "未命名设备";

            return Clip(name, MaxNameChars);
        }

        private static string CleanName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";

            StringBuilder sb = new StringBuilder(raw.Length);
            bool lastWasSpace = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                bool space = c == '\r' || c == '\n' || c == '\t' || c == ' ' || char.IsControl(c);
                if (space)
                {
                    if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                    lastWasSpace = true;
                    continue;
                }
                sb.Append(c);
                lastWasSpace = false;
            }

            return sb.ToString().Trim();
        }

        /// <summary>截到 max 个字符（含省略号），且不把代理对切成两半。</summary>
        private static string Clip(string text, int max)
        {
            if (text == null) return "";
            if (max <= 0) return "";
            if (text.Length <= max) return text;

            int cut = max - 1;                       // 留一个位置给省略号
            if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
            if (cut <= 0) return "…";

            return text.Substring(0, cut) + "…";
        }

        /// <summary>正文硬上限 240 字符，同样不切断代理对。</summary>
        private static string ClampText(string text)
        {
            return Clip(text, MaxTextChars);
        }

        private void Log(string message)
        {
            if (_log == null) return;
            try { _log(message); }
            catch { }
        }

        private static LowBatterySubmitHandler BuildSubmit(NotifyIcon tray)
        {
            NotifyIcon captured = tray;
            return delegate(string title, string text)
            {
                if (captured == null) throw new InvalidOperationException("NotifyIcon 为空，无法提交气泡");
                captured.ShowBalloonTip(BalloonTimeoutMs, title, text, ToolTipIcon.Warning);
            };
        }
    }
}
