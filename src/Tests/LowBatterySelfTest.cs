using System;
using System.Collections.Generic;
using System.Text;
using Voltide.Core;
using Voltide.UI;

namespace Voltide.Tests
{
    /// <summary>
    /// 低电量提醒的离线自检。
    ///
    /// 走的是生产路径：真的 LowBatteryMonitor、真的 LowBatteryNotifier、真的文案生成；
    /// 只把三个外部边界换成可观测的委托（气泡提交、托盘可用性、日志）。判定阈值、
    /// 分组规则、状态转移一次都没有在这里重写 —— 这里只喂快照序列、数事件与提交次数、
    /// 断言卡上写的结果。
    ///
    /// 不创建 DeviceManager、不枚举 HID、不建托盘、不进消息循环。
    /// </summary>
    public static class LowBatterySelfTest
    {
        private const string ContainerA = "{A1B2C3D4-0001-4000-8000-000000000001}";
        private const string ContainerB = "{A1B2C3D4-0001-4000-8000-000000000002}";

        public static int Run(bool json)
        {
            List<CaseResult> cases = new List<CaseResult>();
            int exitCode;

            try
            {
                RunAll(cases);
            }
            catch (Exception ex)
            {
                // 测试自身抛异常也算失败，绝不当成通过。
                CaseResult boom = NewCase("harness-exception", "no exception");
                boom.Failures.Add("测试框架异常: " + ex.GetType().Name + ": " + ex.Message);
                boom.Actual = "exception";
                boom.Passed = false;
                cases.Add(boom);
            }

            int failed = 0;
            bool allPassed = true;
            for (int i = 0; i < cases.Count; i++)
            {
                if (!cases[i].Passed)
                {
                    failed++;
                    allPassed = false;
                }
            }
            exitCode = allPassed ? 0 : 1;

            if (json) Console.WriteLine(ToJson(cases, allPassed, failed));
            else WriteText(cases, allPassed, failed);

            return exitCode;
        }

        // ------------------------------------------------------------- 用例集合

        private static void RunAll(List<CaseResult> cases)
        {
            cases.Add(CaseSequence("seq-21-20-20-19-0", new int[] { 21, 20, 20, 19, 0 }, 1, 1));
            cases.Add(CaseSequence("first-read-20", new int[] { 20 }, 1, 1));
            cases.Add(CaseSequence("seq-25-15", new int[] { 25, 15 }, 1, 1));
            cases.Add(CaseSequence("seq-20-21-20-29-20", new int[] { 20, 21, 20, 29, 20 }, 1, 1));
            cases.Add(CaseSequence("seq-20-30-20", new int[] { 20, 30, 20 }, 2, 2));

            cases.Add(CaseMarkerSurvives("keep-empty-snapshot", 0));
            cases.Add(CaseMarkerSurvives("keep-device-absent", 1));
            cases.Add(CaseMarkerSurvives("keep-offline", 2));
            cases.Add(CaseMarkerSurvives("keep-sleeping", 3));
            cases.Add(CaseMarkerSurvives("keep-error", 4));
            cases.Add(CaseMarkerSurvives("keep-combined", 5));

            cases.Add(CaseDistinctIdentitySameName());
            cases.Add(CaseIdentityCaseVariants(0));
            cases.Add(CaseIdentityCaseVariants(1));
            cases.Add(CaseIdentityCaseVariants(2));
            cases.Add(CaseConflictRows(0));
            cases.Add(CaseConflictRows(1));

            cases.Add(CaseFreshInstanceTwice());
            cases.Add(CaseChargingFirstThenStop());
            cases.Add(CaseChargingNotReset());
            cases.Add(CaseChargingHighResets());
            cases.Add(CaseInvalidBeforeAny());
            cases.Add(CaseInvalidAfterNotified());
            cases.Add(CaseInvalidHighNoReset());
            cases.Add(CaseNullEntries());
            cases.Add(CaseChargingRowSuppressesRound());
            cases.Add(CaseTrayUnavailable());
            cases.Add(CaseSubmitThrows());
            cases.Add(CaseLongNamesAggregate());
            cases.Add(CaseMemoryOnlyState());
        }

        /// <summary>一串百分比，逐轮喂给同一个设备；期望累计 E / B。</summary>
        private static CaseResult CaseSequence(string id, int[] percents, int expectE, int expectB)
        {
            CaseResult c = NewCase(id, "E=" + expectE + ",B=" + expectB);
            Harness h = new Harness();
            DeviceInfo d = WithContainer(ContainerA, "序列鼠标");

            for (int i = 0; i < percents.Length; i++)
            {
                h.Feed(Snaps(Snap(d, percents[i])));
            }

            Expect(c, "E", expectE, h.Events);
            Expect(c, "B", expectB, h.SubmitCalls);
            Expect(c, "S=B", expectB, h.Successes);
            Finish(c, h);
            return c;
        }

        /// <summary>
        /// 已提醒之后经历「无信息」的一轮，再来同键 20：仍只提醒过一次。
        /// mode: 0 空快照 / 1 设备缺席 / 2 离线 / 3 休眠 / 4 错误读数 / 5 以上全部依次
        /// </summary>
        private static CaseResult CaseMarkerSurvives(string id, int mode)
        {
            CaseResult c = NewCase(id, "E=1,B=1");
            Harness h = new Harness();
            DeviceInfo d = WithContainer(ContainerA, "保持标记鼠标");
            DeviceInfo other = WithContainer(ContainerB, "另一台设备");

            h.Feed(Snaps(Snap(d, 20)));

            if (mode == 0 || mode == 5) h.Feed(Empty());
            if (mode == 1 || mode == 5) h.Feed(Snaps(Snap(other, 80)));
            if (mode == 2 || mode == 5) h.Feed(Snaps(Snap(d, 20, false, false, false, null)));
            if (mode == 3 || mode == 5) h.Feed(Snaps(Snap(d, 20, false, true, true, null)));
            if (mode == 4 || mode == 5) h.Feed(Snaps(Snap(d, 20, false, true, false, "读取失败")));

            // 中间那一轮的空/离线/休眠/错误都不得清标记
            if (mode == 2) Expect(c, "offline-round-E", 0, h.LastEventsOfRound);
            if (mode == 4) Expect(c, "error-round-E", 0, h.LastEventsOfRound);

            h.Feed(Snaps(Snap(d, 20)));

            Expect(c, "E", 1, h.Events);
            Expect(c, "B", 1, h.SubmitCalls);
            Expect(c, "marked", 1, h.Notifier.MarkedCount);
            Finish(c, h);
            return c;
        }

        /// <summary>同名、同 VID:PID、身份不同的 A/B 同轮低电：一次气泡里两项，重复该轮不再提醒。</summary>
        private static CaseResult CaseDistinctIdentitySameName()
        {
            CaseResult c = NewCase("distinct-identity-same-name", "E=2,B=1,汇总两项");
            Harness h = new Harness();

            DeviceInfo a = WithContainer(ContainerA, "同名鼠标");
            DeviceInfo b = WithContainer(ContainerB, "同名鼠标");
            // 厂家/型号完全一致，只有物理身份不同
            a.VendorId = 0x046D; a.ProductId = 0xC548;
            b.VendorId = 0x046D; b.ProductId = 0xC548;

            List<DeviceSnapshot> round = Snaps(Snap(a, 20), Snap(b, 15));
            h.Feed(round);

            Expect(c, "E", 2, h.Events);
            Expect(c, "B", 1, h.SubmitCalls);
            ExpectTrue(c, "one-balloon", h.Bodies.Count == 1, "bodies=" + h.Bodies.Count.ToString());

            string body = h.Bodies.Count > 0 ? h.Bodies[0] : "";
            ExpectTrue(c, "body-two-items", CountOf(body, "、") == 1, "body=" + body);
            ExpectTrue(c, "body-two-percents", CountOf(body, "%") == 2, "body=" + body);
            ExpectTrue(c, "body-total-two", body.IndexOf("共 2 台设备电量偏低", StringComparison.Ordinal) >= 0, "body=" + body);

            h.Feed(round);
            Expect(c, "E-after-repeat", 2, h.Events);
            Expect(c, "B-after-repeat", 1, h.SubmitCalls);
            Finish(c, h);
            return c;
        }

        /// <summary>
        /// 同键大小写不同 + 同轮多行 + 后续重复行。branch: 0 container / 1 path / 2 id
        /// </summary>
        private static CaseResult CaseIdentityCaseVariants(int branch)
        {
            string suffix = branch == 0 ? "container" : (branch == 1 ? "path" : "id");
            CaseResult c = NewCase("identity-case-" + suffix, "E=1,B=1");
            Harness h = new Harness();

            DeviceInfo row1;
            DeviceInfo row2;
            if (branch == 0)
            {
                row1 = WithContainer(ContainerA.ToUpperInvariant(), "容器鼠标");
                row2 = WithContainer(ContainerA.ToLowerInvariant(), "容器鼠标");
            }
            else if (branch == 1)
            {
                row1 = WithPath("a&1111&0&0000", "路径鼠标");
                row2 = WithPath("A&1111&0&0000", "路径鼠标");
            }
            else
            {
                row1 = WithId("xinput:Pad-A", "手柄", 0x045E, 0x02EA);
                row2 = WithId("XINPUT:PAD-A", "手柄", 0x045E, 0x02EA);
            }

            ExpectTrue(c, "same-physical-key",
                       DeviceIdentity.PhysicalKey(row1) == DeviceIdentity.PhysicalKey(row2) ||
                       string.Equals(DeviceIdentity.PhysicalKey(row1), DeviceIdentity.PhysicalKey(row2),
                                     StringComparison.OrdinalIgnoreCase),
                       "k1=" + DeviceIdentity.PhysicalKey(row1) + " k2=" + DeviceIdentity.PhysicalKey(row2));

            h.Feed(Snaps(Snap(row1, 20), Snap(row2, 18)));
            Expect(c, "E-round1", 1, h.Events);
            Expect(c, "B-round1", 1, h.SubmitCalls);

            h.Feed(Snaps(Snap(row2, 20), Snap(row1, 18), Snap(row2, 19)));
            Expect(c, "E-round2", 1, h.Events);
            Expect(c, "B-round2", 1, h.SubmitCalls);
            Finish(c, h);
            return c;
        }

        /// <summary>同一物理键同轮出现 20 与 30 两行，颠倒行序并重复刷新。</summary>
        private static CaseResult CaseConflictRows(int order)
        {
            string id = order == 0 ? "conflict-rows-20-30" : "conflict-rows-30-20";
            CaseResult c = NewCase(id, "E=1,B=1");
            Harness h = new Harness();

            DeviceInfo low = WithContainer(ContainerA, "冲突鼠标");
            DeviceInfo high = WithContainer(ContainerA, "冲突鼠标");   // 同一物理身份，第二个接口

            DeviceSnapshot s20 = Snap(low, 20);
            DeviceSnapshot s30 = Snap(high, 30);

            if (order == 0) h.Feed(Snaps(s20, s30));
            else h.Feed(Snaps(s30, s20));

            Expect(c, "E-round1", 1, h.Events);
            Expect(c, "B-round1", 1, h.SubmitCalls);

            // 重复刷新：冲突的 30 行不得把已提醒标记清掉后重发
            if (order == 0) h.Feed(Snaps(s20, s30));
            else h.Feed(Snaps(s30, s20));
            if (order == 0) h.Feed(Snaps(s30, s20));
            else h.Feed(Snaps(s20, s30));

            Expect(c, "E-total", 1, h.Events);
            Expect(c, "B-total", 1, h.SubmitCalls);
            Expect(c, "marked", 1, h.Notifier.MarkedCount);
            Finish(c, h);
            return c;
        }

        /// <summary>两个独立新实例，各自首先读到同一个键的 20：都从空内存状态出发，各自提醒一次。</summary>
        private static CaseResult CaseFreshInstanceTwice()
        {
            CaseResult c = NewCase("fresh-instance-twice", "each E=1,B=1");
            Harness h1 = new Harness();
            Harness h2 = new Harness();
            string bodyPath = "xinput:Pad-A";

            h1.Feed(Snaps(Snap(WithId(bodyPath, "手柄", 0x045E, 0x02EA), 20)));
            h2.Feed(Snaps(Snap(WithId(bodyPath, "手柄", 0x045E, 0x02EA), 20)));

            Expect(c, "E-1", 1, h1.Events);
            Expect(c, "B-1", 1, h1.SubmitCalls);
            Expect(c, "E-2", 1, h2.Events);
            Expect(c, "B-2", 1, h2.SubmitCalls);
            Expect(c, "independent-marks", 2, h1.Notifier.MarkedCount + h2.Notifier.MarkedCount);
            Finish(c, h1);
            return c;
        }

        /// <summary>初始就充电 20：不提醒也不消耗资格；随后停止充电仍 20：取得一次资格。</summary>
        private static CaseResult CaseChargingFirstThenStop()
        {
            CaseResult c = NewCase("charging-first-then-stop", "充电阶段 E=0,B=0；累计 E=1,B=1");
            Harness h = new Harness();
            DeviceInfo d = WithContainer(ContainerA, "充电鼠标");

            h.Feed(Snaps(Snap(d, 20, true, true, false, null)));
            h.Feed(Snaps(Snap(d, 20, true, true, false, null)));
            Expect(c, "charging-E", 0, h.Events);
            Expect(c, "charging-B", 0, h.SubmitCalls);

            h.Feed(Snaps(Snap(d, 20, false, true, false, null)));
            Expect(c, "E", 1, h.Events);
            Expect(c, "B", 1, h.SubmitCalls);
            Finish(c, h);
            return c;
        }

        /// <summary>20 → 充电 20 → 未充电 20：充电本身不重置。</summary>
        private static CaseResult CaseChargingNotReset()
        {
            CaseResult c = NewCase("charging-not-reset", "E=1,B=1");
            Harness h = new Harness();
            DeviceInfo d = WithContainer(ContainerA, "充电鼠标");

            h.Feed(Snaps(Snap(d, 20, false, true, false, null)));
            h.Feed(Snaps(Snap(d, 20, true, true, false, null)));
            h.Feed(Snaps(Snap(d, 20, false, true, false, null)));

            Expect(c, "E", 1, h.Events);
            Expect(c, "B", 1, h.SubmitCalls);
            Finish(c, h);
            return c;
        }

        /// <summary>20 → 充电 30 → 未充电 20：有效 30（即便在充电）可以重置，于是第二次提醒。</summary>
        private static CaseResult CaseChargingHighResets()
        {
            CaseResult c = NewCase("charging-30-resets", "E=2,B=2");
            Harness h = new Harness();
            DeviceInfo d = WithContainer(ContainerA, "充电鼠标");

            h.Feed(Snaps(Snap(d, 20, false, true, false, null)));
            h.Feed(Snaps(Snap(d, 30, true, true, false, null)));
            h.Feed(Snaps(Snap(d, 20, false, true, false, null)));

            Expect(c, "E", 2, h.Events);
            Expect(c, "B", 2, h.SubmitCalls);
            Finish(c, h);
            return c;
        }

        /// <summary>尚未提醒时输入 -1 / 101 / null，随后 20：无效阶段既不提醒也不污染状态。</summary>
        private static CaseResult CaseInvalidBeforeAny()
        {
            CaseResult c = NewCase("invalid-before-any", "无效阶段 E=0,B=0；累计 E=1,B=1");
            Harness h = new Harness();
            DeviceInfo d = WithContainer(ContainerA, "无效读数鼠标");

            h.Feed(Snaps(Snap(d, -1)));
            h.Feed(Snaps(Snap(d, 101)));
            h.Feed(Snaps(Snap(d, null)));
            Expect(c, "invalid-E", 0, h.Events);
            Expect(c, "invalid-B", 0, h.SubmitCalls);
            Expect(c, "invalid-marked", 0, h.Notifier.MarkedCount);

            h.Feed(Snaps(Snap(d, 20)));
            Expect(c, "E", 1, h.Events);
            Expect(c, "B", 1, h.SubmitCalls);
            Finish(c, h);
            return c;
        }

        /// <summary>已提醒 20，经历 -1 / 101 / null，再 20：无效值不误重置。</summary>
        private static CaseResult CaseInvalidAfterNotified()
        {
            CaseResult c = NewCase("invalid-after-notified", "E=1,B=1");
            Harness h = new Harness();
            DeviceInfo d = WithContainer(ContainerA, "无效读数鼠标");

            h.Feed(Snaps(Snap(d, 20)));
            h.Feed(Snaps(Snap(d, -1)));
            h.Feed(Snaps(Snap(d, 101)));
            h.Feed(Snaps(Snap(d, null)));
            h.Feed(Snaps(Snap(d, 20)));

            Expect(c, "E", 1, h.Events);
            Expect(c, "B", 1, h.SubmitCalls);
            Expect(c, "marked", 1, h.Notifier.MarkedCount);
            Finish(c, h);
            return c;
        }

        /// <summary>离线 / 休眠 / 带错误的 100：不得当成有效高电量把标记重置掉。</summary>
        private static CaseResult CaseInvalidHighNoReset()
        {
            CaseResult c = NewCase("invalid-high-no-reset", "E=1,B=1");
            Harness h = new Harness();
            DeviceInfo d = WithContainer(ContainerA, "假高电量鼠标");

            h.Feed(Snaps(Snap(d, 20)));
            Expect(c, "first-B", 1, h.SubmitCalls);

            h.Feed(Snaps(Snap(d, 100, false, false, false, null)));            // 离线 100
            h.Feed(Snaps(Snap(d, 100, false, true, true, null)));              // 休眠 100
            h.Feed(Snaps(Snap(d, 100, false, false, false, "读取失败")));        // 带错误 100
            h.Feed(Snaps(Snap(d, 100, false, true, false, "读取失败")));         // 在线但错误 100

            h.Feed(Snaps(Snap(d, 20)));

            Expect(c, "E", 1, h.Events);
            Expect(c, "B", 1, h.SubmitCalls);
            Expect(c, "marked", 1, h.Notifier.MarkedCount);
            Finish(c, h);
            return c;
        }

        /// <summary>空项、空 Info、空 Reading、空物理键：不抛异常、不产生事件、不改变已有标记。</summary>
        private static CaseResult CaseNullEntries()
        {
            CaseResult c = NewCase("null-entries", "E 不增、B 不增、标记不变");
            Harness h = new Harness();
            DeviceInfo d = WithContainer(ContainerA, "正常鼠标");

            // 先制造一个已提醒标记
            h.Feed(Snaps(Snap(d, 20)));
            Expect(c, "base-E", 1, h.Events);

            DeviceInfo noKey = new DeviceInfo();
            noKey.Name = "没有身份的设备";

            DeviceSnapshot nullInfo = new DeviceSnapshot();
            nullInfo.Info = null;
            nullInfo.Reading = Reading(20, false, true, false, null);

            DeviceSnapshot nullReading = new DeviceSnapshot();
            nullReading.Info = WithContainer(ContainerB, "空读数设备");
            nullReading.Reading = null;

            DeviceSnapshot blankKey = Snap(noKey, 20);

            List<DeviceSnapshot> junk = Empty();
            junk.Add(null);
            junk.Add(nullInfo);
            junk.Add(nullReading);
            junk.Add(blankKey);

            bool threw = false;
            try { h.Feed(junk); }
            catch (Exception ex)
            {
                threw = true;
                c.Failures.Add("junk 输入抛异常: " + ex.GetType().Name + ": " + ex.Message);
            }

            ExpectTrue(c, "no-exception", !threw, "threw=" + threw.ToString());
            Expect(c, "E-after-junk", 1, h.Events);
            Expect(c, "B-after-junk", 1, h.SubmitCalls);
            Expect(c, "marked-after-junk", 1, h.Notifier.MarkedCount);

            h.Feed(Snaps(Snap(d, 20)));
            Expect(c, "E-final", 1, h.Events);
            Expect(c, "B-final", 1, h.SubmitCalls);
            Finish(c, h);
            return c;
        }

        /// <summary>同轮多行中有一条有效行在充电：当轮不提醒；取消充电且仍低电后取得一次资格。</summary>
        private static CaseResult CaseChargingRowSuppressesRound()
        {
            CaseResult c = NewCase("charging-row-suppresses-round", "E=1,B=1");
            Harness h = new Harness();

            DeviceInfo iface1 = WithContainer(ContainerA, "双接口键盘");
            DeviceInfo iface2 = WithContainer(ContainerA, "双接口键盘");

            // 同轮：一条 18% 充电，一条 20% 未充电 —— 任一充电即抑制整组
            h.Feed(Snaps(Snap(iface1, 18, true, true, false, null), Snap(iface2, 20, false, true, false, null)));
            Expect(c, "charging-round-E", 0, h.Events);
            Expect(c, "charging-round-B", 0, h.SubmitCalls);

            // 两条都不充电，仍低电
            h.Feed(Snaps(Snap(iface1, 18, false, true, false, null), Snap(iface2, 20, false, true, false, null)));
            Expect(c, "E", 1, h.Events);
            Expect(c, "B", 1, h.SubmitCalls);
            Finish(c, h);
            return c;
        }

        /// <summary>托盘不可用：资格照记，提交跳过，不循环重试。</summary>
        private static CaseResult CaseTrayUnavailable()
        {
            CaseResult c = NewCase("tray-unavailable", "E=1,B=0,跳过并记录");
            Harness h = new Harness();
            h.Availability = false;
            DeviceInfo d = WithContainer(ContainerA, "无托盘鼠标");

            h.Feed(Snaps(Snap(d, 20)));
            Expect(c, "E-round1", 1, h.Events);
            Expect(c, "B-round1", 0, h.SubmitCalls);
            Expect(c, "skip-round1", 1, h.Skipped);

            h.Feed(Snaps(Snap(d, 19)));
            h.Feed(Snaps(Snap(d, 18)));
            Expect(c, "E-total", 1, h.Events);
            Expect(c, "B-total", 0, h.SubmitCalls);
            Expect(c, "bodies", 0, h.Bodies.Count);
            Finish(c, h);
            return c;
        }

        /// <summary>提交委托抛异常：E=1,B=1,S=0，异常被捕获，不循环重试。</summary>
        private static CaseResult CaseSubmitThrows()
        {
            CaseResult c = NewCase("submit-throws", "E=1,B=1,S=0,异常被捕获");
            Harness h = new Harness();
            h.ThrowOnSubmit = true;
            DeviceInfo d = WithContainer(ContainerA, "异常气泡鼠标");

            bool threw = false;
            try { h.Feed(Snaps(Snap(d, 20))); }
            catch (Exception ex)
            {
                threw = true;
                c.Failures.Add("提交异常没有被内部捕获: " + ex.GetType().Name + ": " + ex.Message);
            }

            ExpectTrue(c, "no-escape", !threw, "threw=" + threw.ToString());
            Expect(c, "E", 1, h.Events);
            Expect(c, "B", 1, h.SubmitCalls);
            Expect(c, "S", 0, h.Successes);
            Expect(c, "failed-flag", 1, h.Failed);

            h.Feed(Snaps(Snap(d, 19)));
            h.Feed(Snaps(Snap(d, 18)));
            Expect(c, "E-total", 1, h.Events);
            Expect(c, "B-total", 1, h.SubmitCalls);
            Finish(c, h);
            return c;
        }

        /// <summary>长名称 / 多台 / 名称含引号或换行：一条汇总、正文≤240、可 JSON 转义、被省略者也完成去重。</summary>
        private static CaseResult CaseLongNamesAggregate()
        {
            CaseResult c = NewCase("long-names-multi", "E=5,B=1,正文<=240");
            Harness h = new Harness();

            List<DeviceSnapshot> round = Empty();

            DeviceInfo d1 = WithContainer("{11111111-0000-4000-8000-00000000000A}", "超长名称无线游戏鼠标第二代黑色版本带RGB灯效特别版");
            DeviceInfo d2 = WithContainer("{11111111-0000-4000-8000-00000000000B}", "Logitech \"G Pro\" 无线鼠标");
            DeviceInfo d3 = WithContainer("{11111111-0000-4000-8000-00000000000C}", "机械键盘\n第二行名字");
            DeviceInfo d4 = WithContainer("{11111111-0000-4000-8000-00000000000D}", "耳机甲");
            DeviceInfo d5 = WithContainer("{11111111-0000-4000-8000-00000000000E}", "耳机乙");

            round.Add(Snap(d1, 20));
            round.Add(Snap(d2, 15));
            round.Add(Snap(d3, 18));
            round.Add(Snap(d4, 12));
            round.Add(Snap(d5, 9));

            h.Feed(round);
            Expect(c, "E", 5, h.Events);
            Expect(c, "B", 1, h.SubmitCalls);
            ExpectTrue(c, "one-balloon", h.Bodies.Count == 1, "bodies=" + h.Bodies.Count.ToString());

            string body = h.Bodies.Count > 0 ? h.Bodies[0] : "";
            ExpectTrue(c, "body-len", body.Length > 0 && body.Length <= LowBatteryNotifier.MaxTextChars,
                       "len=" + body.Length.ToString());
            ExpectTrue(c, "body-no-crlf", body.IndexOf('\r') < 0 && body.IndexOf('\n') < 0,
                       "body has CR/LF");
            ExpectTrue(c, "body-total-five", body.IndexOf("共 5 台设备电量偏低", StringComparison.Ordinal) >= 0, "body=" + body);
            ExpectTrue(c, "body-omitted-summary", body.IndexOf("等共 5 台", StringComparison.Ordinal) >= 0, "body=" + body);
            ExpectTrue(c, "body-three-listed", CountOf(body, "、") == 2, "body=" + body);
            ExpectTrue(c, "json-escapable", Escape(body).Length > 0, "escape failed");

            // 重复该轮：被省略展示的两台也必须已经完成本轮去重
            h.Feed(round);
            Expect(c, "E-after-repeat", 5, h.Events);
            Expect(c, "B-after-repeat", 1, h.SubmitCalls);
            Expect(c, "marked", 5, h.Notifier.MarkedCount);
            Finish(c, h);
            return c;
        }

        /// <summary>通知状态只在内存：新实例没有可恢复的状态，也没有任何落盘/注册表路径参与。</summary>
        private static CaseResult CaseMemoryOnlyState()
        {
            CaseResult c = NewCase("memory-only-state", "两次新实例各 E=1,B=1；空实例 MarkedCount=0");
            DeviceInfo d = WithContainer(ContainerA, "内存鼠标");

            Harness h1 = new Harness();
            h1.Feed(Snaps(Snap(d, 20)));
            Expect(c, "E-1", 1, h1.Events);
            Expect(c, "B-1", 1, h1.SubmitCalls);

            Harness h2 = new Harness();
            h2.Feed(Snaps(Snap(d, 20)));
            Expect(c, "E-2", 1, h2.Events);
            Expect(c, "B-2", 1, h2.SubmitCalls);

            Harness fresh = new Harness();
            fresh.Feed(Empty());
            Expect(c, "fresh-marked", 0, fresh.Notifier.MarkedCount);

            Finish(c, h1);
            return c;
        }

        // ------------------------------------------------------------------ 装置

        private static DeviceInfo WithContainer(string containerId, string name)
        {
            DeviceInfo d = new DeviceInfo();
            d.Id = "dev-" + name;
            d.Name = name;
            d.Brand = "TestBrand";
            d.Model = "TestModel";
            d.ShortModel = "TestShort";
            d.ContainerId = containerId;
            d.VendorId = 0x046D;
            d.ProductId = 0xC548;
            return d;
        }

        private static DeviceInfo WithPath(string instance, string name)
        {
            DeviceInfo d = new DeviceInfo();
            d.Id = "dev-" + name;
            d.Name = name;
            d.Brand = "TestBrand";
            d.Model = "TestModel";
            d.HidPath = @"\\?\hid#vid_3554&pid_fa09&mi_01&col01#" + instance +
                        "#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            d.VendorId = 0x3554;
            d.ProductId = 0xFA09;
            return d;
        }

        private static DeviceInfo WithId(string id, string name, ushort vid, ushort pid)
        {
            DeviceInfo d = new DeviceInfo();
            d.Id = id;
            d.Name = name;
            d.Brand = "TestBrand";
            d.Model = "TestModel";
            d.VendorId = vid;
            d.ProductId = pid;
            return d;
        }

        private static DeviceReading Reading(int? percent, bool charging, bool online, bool sleeping, string error)
        {
            DeviceReading r = new DeviceReading();
            r.Percent = percent;
            r.Charging = charging;
            r.ChargingKnown = true;
            r.Online = online;
            r.Sleeping = sleeping;
            r.Error = error;
            r.UpdatedAt = DateTime.Now;
            return r;
        }

        private static DeviceSnapshot Snap(DeviceInfo info, int? percent)
        {
            return Snap(info, percent, false, true, false, null);
        }

        private static DeviceSnapshot Snap(DeviceInfo info, int? percent, bool charging,
                                           bool online, bool sleeping, string error)
        {
            DeviceSnapshot s = new DeviceSnapshot();
            s.Info = info;
            s.Reading = Reading(percent, charging, online, sleeping, error);
            return s;
        }

        private static List<DeviceSnapshot> Snaps(params DeviceSnapshot[] items)
        {
            List<DeviceSnapshot> list = new List<DeviceSnapshot>();
            if (items != null)
            {
                for (int i = 0; i < items.Length; i++) list.Add(items[i]);
            }
            return list;
        }

        private static List<DeviceSnapshot> Empty()
        {
            return new List<DeviceSnapshot>();
        }

        /// <summary>把生产协调路径包一层，累计 E / B / S 并记录提交过的正文。</summary>
        private class Harness
        {
            public int Events;
            public int SubmitCalls;
            public int Successes;
            public int Skipped;
            public int Failed;
            public int LastEventsOfRound;
            public bool Availability = true;
            public bool ThrowOnSubmit;
            public List<string> Bodies = new List<string>();
            public LowBatteryNotifier Notifier;

            public Harness()
            {
                LowBatterySubmitHandler submit = delegate(string title, string text)
                {
                    if (ThrowOnSubmit) throw new InvalidOperationException("离线测试：模拟 ShowBalloonTip 抛异常");
                    Bodies.Add(text);
                };

                LowBatteryAvailabilityHandler available = delegate { return Availability; };
                LowBatteryLogHandler log = delegate(string message) { };

                Notifier = new LowBatteryNotifier(submit, available, log);
            }

            public LowBatteryCycleResult Feed(List<DeviceSnapshot> snapshots)
            {
                LowBatteryCycleResult r = Notifier.OnSnapshot(snapshots);
                Events += r.Events;
                LastEventsOfRound = r.Events;
                SubmitCalls += r.SubmitCalls;
                Successes += r.SubmitSuccesses;
                if (r.SkippedUnavailable) Skipped++;
                if (r.SubmitFailed) Failed++;
                return r;
            }
        }

        // ---------------------------------------------------------------- 结果

        private class CaseResult
        {
            public string Id;
            public string Expected;
            public string Actual;
            public bool Passed;
            public List<string> Failures = new List<string>();
        }

        private static CaseResult NewCase(string id, string expected)
        {
            CaseResult c = new CaseResult();
            c.Id = id;
            c.Expected = expected;
            c.Actual = "";
            return c;
        }

        private static void Expect(CaseResult c, string label, int expected, int actual)
        {
            if (expected == actual) return;
            c.Failures.Add(label + ": 期望 " + expected.ToString() + "，实际 " + actual.ToString());
        }

        private static void ExpectTrue(CaseResult c, string label, bool condition, string detail)
        {
            if (condition) return;
            c.Failures.Add(label + ": " + detail);
        }

        private static void Finish(CaseResult c, Harness h)
        {
            if (c.Actual.Length == 0)
            {
                c.Actual = "E=" + h.Events.ToString() + ",B=" + h.SubmitCalls.ToString() +
                           ",S=" + h.Successes.ToString();
            }
            c.Passed = c.Failures.Count == 0;
        }

        private static int CountOf(string text, string needle)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return 0;
            int count = 0;
            int at = 0;
            while (true)
            {
                int found = text.IndexOf(needle, at, StringComparison.Ordinal);
                if (found < 0) break;
                count++;
                at = found + needle.Length;
            }
            return count;
        }

        // ---------------------------------------------------------------- 输出

        private static void WriteText(List<CaseResult> cases, bool allPassed, int failed)
        {
            Console.WriteLine("low-battery self test");
            Console.WriteLine("cases: " + cases.Count.ToString() + "  failed: " + failed.ToString());
            for (int i = 0; i < cases.Count; i++)
            {
                CaseResult c = cases[i];
                Console.WriteLine((c.Passed ? "PASS " : "FAIL ") + c.Id +
                                  "  expected=" + c.Expected + "  actual=" + c.Actual);
                for (int f = 0; f < c.Failures.Count; f++)
                {
                    Console.WriteLine("       " + c.Failures[f]);
                }
            }
            Console.WriteLine(allPassed ? "RESULT: PASS" : "RESULT: FAIL");
        }

        private static string ToJson(List<CaseResult> cases, bool allPassed, int failed)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"mode\":\"test-low-battery\",");
            sb.Append("\"processId\":").Append(System.Diagnostics.Process.GetCurrentProcess().Id.ToString(Invariant)).Append(',');
            sb.Append("\"generatedAtLocal\":\"").Append(Escape(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))).Append("\",");
            sb.Append("\"exe\":\"").Append(Escape(ExePath())).Append("\",");
            sb.Append("\"passed\":").Append(allPassed ? "true" : "false").Append(',');
            sb.Append("\"caseCount\":").Append(cases.Count.ToString(Invariant)).Append(',');
            sb.Append("\"failedCount\":").Append(failed.ToString(Invariant)).Append(',');

            sb.Append("\"cases\":[");
            for (int i = 0; i < cases.Count; i++)
            {
                if (i != 0) sb.Append(',');
                CaseResult c = cases[i];
                sb.Append('{');
                sb.Append("\"id\":\"").Append(Escape(c.Id)).Append("\",");
                sb.Append("\"expected\":\"").Append(Escape(c.Expected)).Append("\",");
                sb.Append("\"actual\":\"").Append(Escape(c.Actual)).Append("\",");
                sb.Append("\"passed\":").Append(c.Passed ? "true" : "false").Append(',');
                sb.Append("\"failures\":[");
                for (int f = 0; f < c.Failures.Count; f++)
                {
                    if (f != 0) sb.Append(',');
                    sb.Append('"').Append(Escape(c.Failures[f])).Append('"');
                }
                sb.Append("]}");
            }
            sb.Append(']');

            sb.Append(",\"failures\":[");
            bool first = true;
            for (int i = 0; i < cases.Count; i++)
            {
                for (int f = 0; f < cases[i].Failures.Count; f++)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(Escape(cases[i].Id + ": " + cases[i].Failures[f])).Append('"');
                }
            }
            sb.Append("]}");

            return sb.ToString();
        }

        private static readonly System.Globalization.CultureInfo Invariant =
            System.Globalization.CultureInfo.InvariantCulture;

        private static string ExePath()
        {
            try { return System.Reflection.Assembly.GetEntryAssembly().Location; }
            catch { return ""; }
        }

        /// <summary>
        /// 纯 ASCII 转义：非 ASCII 一律 \uXXXX，控制字符走短转义。
        /// 这样 stdout 不受控制台代码页影响，任何解析器（含 PowerShell 5.1）都能直接读。
        /// </summary>
        private static string Escape(string value)
        {
            if (value == null) return "";
            StringBuilder sb = new StringBuilder(value.Length + 16);
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
                        if (c < ' ' || c > '~')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", Invariant));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
