using System;
using System.Collections.Generic;

namespace Voltide.Core
{
    /// <summary>
    /// 一次「低电量资格」：某个物理设备在本轮首次跌破阈值、需要提醒。
    /// 只描述事实（哪个物理键、本轮最低电量、代表设备信息），不含任何文案。
    /// </summary>
    public class LowBatteryEvent
    {
        /// <summary>DeviceIdentity.PhysicalKey 产出的物理身份键。</summary>
        public string PhysicalKey;

        /// <summary>本轮该物理键下所有有效读数中的最低百分比。</summary>
        public int Percent;

        /// <summary>该物理键下第一个有效行对应的设备信息，供文案取名称。</summary>
        public DeviceInfo Info;
    }

    /// <summary>
    /// 低电量状态机（纯内存，不落盘）。
    ///
    /// 规则集中在这里，只有一处实现：判定「有效读数」、按物理键分组、每台设备每个低电
    /// 周期只提醒一次、有效读数回到 30% 才重置。跨重启不持久化是有意为之 —— 重启后仍
    /// 低电允许再提醒一次，所以状态就是一个 Dictionary，进程结束即消失。
    ///
    /// 刻意不做的事：
    ///   * 不读 Registry / 配置文件 / devices.json（通知状态绝不落盘）；
    ///   * 不用显示名称或 VID:PID 当身份（同名同型号的两台设备必须是两台）；
    ///   * 不缓存电量：没有新鲜有效读数就是「无信息」，而不是「沿用上次」。
    ///
    /// 线程约定：只在 UI 线程串行调用（托盘刷新回调），内部不加锁。
    /// </summary>
    public class LowBatteryMonitor
    {
        /// <summary>≤ 这个百分比即提醒。</summary>
        public const int LowPercent = 20;

        /// <summary>有效读数 ≥ 这个百分比才清除该设备的已提醒标记。</summary>
        public const int ResetPercent = 30;

        // 已提醒的物理键。OrdinalIgnoreCase：物理键来自设备路径/容器 id，大小写不敏感。
        private readonly Dictionary<string, bool> _notified =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前处于「已提醒」状态的物理键数量（诊断用，不参与判断）。</summary>
        public int MarkedCount
        {
            get { return _notified.Count; }
        }

        /// <summary>某个物理键是否已经被本轮低电周期提醒过。</summary>
        public bool IsMarked(string physicalKey)
        {
            if (string.IsNullOrEmpty(physicalKey)) return false;
            return _notified.ContainsKey(physicalKey);
        }

        /// <summary>
        /// 用一份快照推进状态机，返回本轮新产生的资格事件（可能为空）。
        ///
        /// 分两趟：先把所有有效行按物理键分组（组内取最低百分比、任一充电即视为充电），
        /// 再对每个物理键做且只做一次状态转移。分两趟是必须的 —— 同一个接收器会暴露多个
        /// HID 接口，同轮里可能出现 20% 与 30% 两行；若逐行转移，30% 那行会先把标记清掉，
        /// 20% 那行紧接着又提醒一次。组内取最低值就消除这种遍历顺序依赖。
        /// </summary>
        public List<LowBatteryEvent> Evaluate(List<DeviceSnapshot> snapshots)
        {
            List<LowBatteryEvent> events = new List<LowBatteryEvent>();
            if (snapshots == null) return events;

            Dictionary<string, CycleGroup> groups =
                new Dictionary<string, CycleGroup>(StringComparer.OrdinalIgnoreCase);
            List<string> order = new List<string>();

            // 第一趟：只收集有效行，按物理键归组。
            for (int i = 0; i < snapshots.Count; i++)
            {
                DeviceSnapshot snap = snapshots[i];
                if (snap == null) continue;

                DeviceInfo info = snap.Info;
                DeviceReading reading = snap.Reading;
                if (info == null || reading == null) continue;

                string key = DeviceIdentity.PhysicalKey(info);
                if (key == null) continue;
                key = key.Trim();
                if (key.Length == 0) continue;

                if (!IsValidReading(reading)) continue;

                int percent = reading.Percent.Value;

                CycleGroup group;
                if (!groups.TryGetValue(key, out group))
                {
                    group = new CycleGroup();
                    group.Key = key;
                    group.Percent = percent;
                    group.Info = info;
                    groups[key] = group;
                    order.Add(key);
                }
                else if (percent < group.Percent)
                {
                    group.Percent = percent;
                    // 用最低那一行的设备信息做文案代表，名称才与展示的电量对得上。
                    group.Info = info;
                }

                if (reading.Charging) group.Charging = true;
            }

            // 第二趟：每个物理键每轮最多一次状态转移。
            for (int i = 0; i < order.Count; i++)
            {
                CycleGroup group = groups[order[i]];
                int percent = group.Percent;

                // ≥30% 是唯一的重置条件；充电中的高电量同样算有效读数。
                if (percent >= ResetPercent)
                {
                    _notified.Remove(group.Key);
                    continue;
                }

                // 21%—29%：保持现状，既不清标记也不再报。
                if (percent > LowPercent) continue;

                // 充电中的低电量不提醒，也不消耗「尚未取得的」提醒资格。
                if (group.Charging) continue;

                if (_notified.ContainsKey(group.Key)) continue;

                _notified[group.Key] = true;

                LowBatteryEvent ev = new LowBatteryEvent();
                ev.PhysicalKey = group.Key;
                ev.Percent = percent;
                ev.Info = group.Info;
                events.Add(ev);
            }

            return events;
        }

        /// <summary>
        /// 有效读数：设备在线、没休眠、有百分比、百分比在 0—100 之间、没有错误。
        ///
        /// ChargingKnown 不参与判定（它只表示「充电状态没问出来」，不代表电量不可信）；
        /// BatteryLevel 与卡片文案也不参与 —— 只有 Percent 是电量事实。
        /// </summary>
        public static bool IsValidReading(DeviceReading reading)
        {
            if (reading == null) return false;
            if (!reading.Online) return false;
            if (reading.Sleeping) return false;
            if (!reading.Percent.HasValue) return false;
            if (!string.IsNullOrEmpty(reading.Error)) return false;

            int percent = reading.Percent.Value;
            if (percent < 0 || percent > 100) return false;

            return true;
        }

        /// <summary>同一物理键在一轮快照里的汇总结果。</summary>
        private class CycleGroup
        {
            public string Key;
            public int Percent;
            public bool Charging;
            public DeviceInfo Info;
        }
    }
}
