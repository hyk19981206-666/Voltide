using System;
using System.Collections.Generic;
using System.Text;
using Voltide.Core;
using Voltide.Interop;
using Voltide.Providers;

namespace Voltide.Tests
{
    /// <summary>
    /// DualSense / DS5Dongle 协议与发现逻辑的离线自检。
    ///
    /// 直接调用生产代码 DualSenseProtocol / DualSenseProvider.Discover / LowBatteryMonitor，
    /// 不碰真实 HID 设备、不建托盘、不进消息循环。全部通过退出 0，任一失败退出 1。
    /// </summary>
    public static class DualSenseSelfTest
    {
        public static int Run(bool json)
        {
            List<CaseResult> cases = new List<CaseResult>();
            try
            {
                RunAll(cases);
            }
            catch (Exception ex)
            {
                CaseResult boom = new CaseResult("harness-exception");
                boom.Failures.Add(ex.GetType().Name + ": " + ex.Message);
                cases.Add(boom);
            }

            int failed = 0;
            for (int i = 0; i < cases.Count; i++)
            {
                if (!cases[i].Passed) failed++;
            }

            bool allPassed = failed == 0;
            if (json) Console.WriteLine(ToJson(cases, allPassed, failed));
            else WriteText(cases, allPassed, failed);
            return allPassed ? 0 : 1;
        }

        private static void RunAll(List<CaseResult> cases)
        {
            cases.Add(CaseDischargingAllLevels());
            cases.Add(CaseRealCapturedFrame());
            cases.Add(CaseChargingAllLevels());
            cases.Add(CaseCompleteFull());
            cases.Add(CaseRejectInvalidRawLevels());
            cases.Add(CaseRejectFaultPowerStates());
            cases.Add(CaseRejectMalformedFrames());
            cases.Add(CaseDongleVersionF8());
            cases.Add(CaseDiscoverFilteringAndDedup());
            cases.Add(CaseLowBatteryIntegration());
        }

        private static byte[] MakeUsbFrame(byte status0, byte status1)
        {
            byte[] buf = new byte[DualSenseProtocol.UsbInputReportLength];
            buf[0] = DualSenseProtocol.UsbInputReportId;
            buf[DualSenseProtocol.BatteryByteOffset] = status0;
            buf[DualSenseProtocol.Status1ByteOffset] = status1;
            return buf;
        }

        private static CaseResult CaseDischargingAllLevels()
        {
            CaseResult c = new CaseResult("discharging-levels-0-to-10");
            int[] expected = new int[] { 5, 15, 25, 35, 45, 55, 65, 75, 85, 95, 100 };

            for (int raw = 0; raw <= 10; raw++)
            {
                int pct;
                bool chg;
                byte s0;
                byte s1;
                string reason;
                bool ok = DualSenseProtocol.TryParseUsbReport(
                    MakeUsbFrame((byte)raw, 0x00), out pct, out chg, out s0, out s1, out reason);

                if (!ok) c.Failures.Add("raw=" + raw + " failed: " + reason);
                else if (pct != expected[raw]) c.Failures.Add("raw=" + raw + " expected=" + expected[raw] + " actual=" + pct);
                else if (chg) c.Failures.Add("raw=" + raw + " expected charging=false");
            }
            return c;
        }

        private static CaseResult CaseRealCapturedFrame()
        {
            CaseResult c = new CaseResult("real-captured-ds5dongle-frame");
            // Captured USB report 0x01 sample (DS5Dongle v0.7.2, PID 0DF2).
            byte[] real = new byte[]
            {
                0x01, 0x80, 0x80, 0x83, 0x82, 0x00, 0x00, 0x01,
                0x08, 0x00, 0x00, 0x00, 0x75, 0x33, 0x95, 0x70,
                0xFD, 0xFF, 0x03, 0x00, 0xFD, 0xFF, 0x7F, 0xF6,
                0x10, 0x1F, 0x17, 0x00, 0xCD, 0xEF, 0x86, 0x62,
                0x0F, 0x80, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x3C, 0x50, 0x08, 0x03, 0x07, 0x00, 0x00,
                0x23, 0xB2, 0xA1, 0xEE, 0xD5, 0x3A, 0x0E, 0x11
            };

            int pct;
            bool chg;
            byte s0;
            byte s1;
            string reason;
            bool ok = DualSenseProtocol.TryParseUsbReport(real, out pct, out chg, out s0, out s1, out reason);

            if (!ok) c.Failures.Add("parse failed: " + reason);
            if (pct != 75) c.Failures.Add("expected 75%, actual=" + pct);
            if (chg) c.Failures.Add("expected charging=false");
            if (s0 != 0x07) c.Failures.Add("expected s0=0x07, actual=0x" + s0.ToString("X2"));
            if (s1 != 0x00) c.Failures.Add("expected s1=0x00, actual=0x" + s1.ToString("X2"));
            return c;
        }

        private static CaseResult CaseChargingAllLevels()
        {
            CaseResult c = new CaseResult("charging-levels-0-to-10");
            int[] expected = new int[] { 5, 15, 25, 35, 45, 55, 65, 75, 85, 95, 100 };

            for (int raw = 0; raw <= 10; raw++)
            {
                int pct;
                bool chg;
                byte s0;
                byte s1;
                string reason;
                bool ok = DualSenseProtocol.TryParseUsbReport(
                    MakeUsbFrame((byte)(0x10 | raw), 0x10), out pct, out chg, out s0, out s1, out reason);

                if (!ok) c.Failures.Add("raw=" + raw + " failed: " + reason);
                else if (pct != expected[raw]) c.Failures.Add("raw=" + raw + " expected=" + expected[raw] + " actual=" + pct);
                else if (!chg) c.Failures.Add("raw=" + raw + " expected charging=true");
            }
            return c;
        }

        private static CaseResult CaseCompleteFull()
        {
            CaseResult c = new CaseResult("power-state-complete-full");
            byte[] samples = new byte[] { 0x20, 0x29, 0x2A, 0x2F };
            for (int i = 0; i < samples.Length; i++)
            {
                int pct;
                bool chg;
                byte s0;
                byte s1;
                string reason;
                bool ok = DualSenseProtocol.TryParseUsbReport(
                    MakeUsbFrame(samples[i], 0x18), out pct, out chg, out s0, out s1, out reason);

                if (!ok) c.Failures.Add("0x" + samples[i].ToString("X2") + " failed: " + reason);
                else if (pct != 100 || !chg) c.Failures.Add("0x" + samples[i].ToString("X2") + " expected 100% charging");
            }
            return c;
        }

        private static CaseResult CaseRejectInvalidRawLevels()
        {
            CaseResult c = new CaseResult("reject-invalid-raw-levels");
            byte[] bad = new byte[] { 0x0B, 0x0C, 0x0F, 0x1B, 0x1C, 0x1F };
            for (int i = 0; i < bad.Length; i++)
            {
                int pct;
                bool chg;
                byte s0;
                byte s1;
                string reason;
                bool ok = DualSenseProtocol.TryParseUsbReport(
                    MakeUsbFrame(bad[i], 0x00), out pct, out chg, out s0, out s1, out reason);

                if (ok) c.Failures.Add("0x" + bad[i].ToString("X2") + " should be rejected, got " + pct + "%");
                else if (string.IsNullOrEmpty(reason)) c.Failures.Add("0x" + bad[i].ToString("X2") + " missing reason");
            }
            return c;
        }

        private static CaseResult CaseRejectFaultPowerStates()
        {
            CaseResult c = new CaseResult("reject-fault-power-states");
            byte[] faults = new byte[] { 0xA0, 0xB5, 0xF0, 0x35, 0x90 };
            for (int i = 0; i < faults.Length; i++)
            {
                int pct;
                bool chg;
                byte s0;
                byte s1;
                string reason;
                bool ok = DualSenseProtocol.TryParseUsbReport(
                    MakeUsbFrame(faults[i], 0x00), out pct, out chg, out s0, out s1, out reason);

                if (ok) c.Failures.Add("0x" + faults[i].ToString("X2") + " should be rejected, got " + pct + "%");
                else if (string.IsNullOrEmpty(reason)) c.Failures.Add("0x" + faults[i].ToString("X2") + " missing reason");
            }
            return c;
        }

        private static CaseResult CaseRejectMalformedFrames()
        {
            CaseResult c = new CaseResult("reject-malformed-frames");

            int pct;
            bool chg;
            byte s0;
            byte s1;
            string reason;

            if (DualSenseProtocol.TryParseUsbReport(null, out pct, out chg, out s0, out s1, out reason))
                c.Failures.Add("null frame should fail");

            byte[] tooShort = new byte[54];
            tooShort[0] = 0x01;
            tooShort[53] = 0x07;
            if (DualSenseProtocol.TryParseUsbReport(tooShort, out pct, out chg, out s0, out s1, out reason))
                c.Failures.Add("54-byte frame should fail");

            byte[] btReport = new byte[78];
            btReport[0] = 0x31;
            btReport[53] = 0x07;
            if (DualSenseProtocol.TryParseUsbReport(btReport, out pct, out chg, out s0, out s1, out reason))
                c.Failures.Add("reportId 0x31 should fail USB parser");

            byte[] zeroId = MakeUsbFrame(0x07, 0x00);
            zeroId[0] = 0x00;
            if (DualSenseProtocol.TryParseUsbReport(zeroId, out pct, out chg, out s0, out s1, out reason))
                c.Failures.Add("reportId 0x00 should fail");

            return c;
        }

        private static CaseResult CaseDongleVersionF8()
        {
            CaseResult c = new CaseResult("dongle-version-f8");

            byte[] valid = new byte[64];
            valid[0] = 0xF8;
            byte[] ascii = Encoding.ASCII.GetBytes("v0.7.2");
            Array.Copy(ascii, 0, valid, 1, ascii.Length);

            string ver;
            if (!DualSenseProtocol.TryParseDongleVersion(valid, out ver) || ver != "v0.7.2")
                c.Failures.Add("expected v0.7.2, actual=" + ver);

            byte[] wrongId = new byte[64];
            wrongId[0] = 0x20;
            Array.Copy(ascii, 0, wrongId, 1, ascii.Length);
            if (DualSenseProtocol.TryParseDongleVersion(wrongId, out ver))
                c.Failures.Add("wrong report id 0x20 should fail");

            byte[] empty = new byte[64];
            empty[0] = 0xF8;
            if (DualSenseProtocol.TryParseDongleVersion(empty, out ver))
                c.Failures.Add("empty F8 payload should fail");

            byte[] binaryNoise = new byte[] { 0xF8, 0x01, 0xFF, 0x00 };
            if (DualSenseProtocol.TryParseDongleVersion(binaryNoise, out ver))
                c.Failures.Add("non-ASCII F8 payload should fail");

            return c;
        }

        private static CaseResult CaseDiscoverFilteringAndDedup()
        {
            CaseResult c = new CaseResult("discover-filtering-and-dedup");

            List<HidDeviceInfo> catalog = new List<HidDeviceInfo>();

            // Valid Edge gamepad + duplicate same container
            catalog.Add(MakeHid(0x054C, 0x0DF2, 0x0001, 0x0005, 64, "{11111111-0000-0000-0000-000000000001}", @"\\?\hid#vid_054c&pid_0df2&mi_03#a&1#{guid}", "DualSense Edge Wireless Controller"));
            catalog.Add(MakeHid(0x054C, 0x0DF2, 0x0001, 0x0005, 64, "{11111111-0000-0000-0000-000000000001}", @"\\?\hid#vid_054c&pid_0df2&mi_03#a&2#{guid}", "DualSense Edge Wireless Controller"));

            // Valid standard DualSense gamepad
            catalog.Add(MakeHid(0x054C, 0x0CE6, 0x0001, 0x0005, 64, "{22222222-0000-0000-0000-000000000002}", @"\\?\hid#vid_054c&pid_0ce6&mi_03#b&1#{guid}", "DualSense Wireless Controller"));

            // Must be ignored: wake keyboard, consumer control, BT 78-byte report, DS4 PID, other vendor
            catalog.Add(MakeHid(0x054C, 0x0DF2, 0x0001, 0x0006, 9, "{11111111-0000-0000-0000-000000000001}", @"\\?\hid#vid_054c&pid_0df2&mi_04#a&3#{guid}", "DS5 Dongle Wake Keyboard"));
            catalog.Add(MakeHid(0x054C, 0x0DF2, 0x000C, 0x0001, 3, "{11111111-0000-0000-0000-000000000001}", @"\\?\hid#vid_054c&pid_0df2&mi_05#a&4#{guid}", "DS5 Dongle Consumer"));
            catalog.Add(MakeHid(0x054C, 0x0CE6, 0x0001, 0x0005, 78, "{33333333-0000-0000-0000-000000000003}", @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0ce6#c&1#{guid}", "DualSense Wireless Controller"));
            catalog.Add(MakeHid(0x054C, 0x09CC, 0x0001, 0x0005, 64, "{44444444-0000-0000-0000-000000000004}", @"\\?\hid#vid_054c&pid_09cc&mi_03#d&1#{guid}", "Wireless Controller"));
            catalog.Add(MakeHid(0x24AE, 0x14A5, 0x0001, 0x0005, 64, "{55555555-0000-0000-0000-000000000005}", @"\\?\hid#vid_24ae&pid_14a5&mi_01#e&1#{guid}", "Other"));

            DualSenseProvider provider = new DualSenseProvider();
            List<DeviceInfo> found = provider.Discover(catalog);

            if (found.Count != 2)
            {
                c.Failures.Add("expected 2 devices, actual=" + found.Count);
                return c;
            }

            DeviceInfo edge = found[0];
            if (edge.ProductId != 0x0DF2 || edge.Brand != "SONY" || edge.Model != "DualSense Edge" ||
                edge.ShortModel != "DS5 Edge" || edge.Kind != DeviceKind.Other)
            {
                c.Failures.Add("edge metadata mismatch: " + edge.Brand + "/" + edge.Model + "/" + edge.ShortModel);
            }

            DeviceInfo std = found[1];
            if (std.ProductId != 0x0CE6 || std.Brand != "SONY" || std.Model != "DualSense" ||
                std.ShortModel != "DualSense" || std.Kind != DeviceKind.Other)
            {
                c.Failures.Add("standard metadata mismatch: " + std.Brand + "/" + std.Model + "/" + std.ShortModel);
            }

            return c;
        }

        private static CaseResult CaseLowBatteryIntegration()
        {
            CaseResult c = new CaseResult("low-battery-monitor-integration");

            LowBatteryMonitor monitor = new LowBatteryMonitor();
            DeviceInfo info = new DeviceInfo();
            info.Id = "dualsense-test";
            info.Name = "Sony DualSense Edge";
            info.Brand = "SONY";
            info.Model = "DualSense Edge";
            info.ShortModel = "DS5 Edge";
            info.ContainerId = "{A1B2C3D4-0001-4000-8000-000000000099}";

            // 1) Charging at raw=1 (15%): must NOT trigger
            List<LowBatteryEvent> ev1 = monitor.Evaluate(Wrap(info, MakeUsbFrame(0x11, 0x10)));
            if (ev1.Count != 0) c.Failures.Add("charging at 15% should not trigger, got " + ev1.Count);

            // 2) Discharging at raw=1 (15%): MUST trigger once
            List<LowBatteryEvent> ev2 = monitor.Evaluate(Wrap(info, MakeUsbFrame(0x01, 0x00)));
            if (ev2.Count != 1 || ev2[0].Percent != 15) c.Failures.Add("discharging at 15% expected 1 event @15%");

            // 3) Discharging at raw=0 (5%): already marked, must NOT re-trigger
            List<LowBatteryEvent> ev3 = monitor.Evaluate(Wrap(info, MakeUsbFrame(0x00, 0x00)));
            if (ev3.Count != 0) c.Failures.Add("discharging at 5% while marked should not re-trigger");

            // 4) Recover to raw=3 (35% >= 30% reset) then drop to raw=1 (15%): triggers second time
            List<LowBatteryEvent> ev4 = monitor.Evaluate(Wrap(info, MakeUsbFrame(0x03, 0x00)));
            if (ev4.Count != 0) c.Failures.Add("35% should not trigger");
            List<LowBatteryEvent> ev5 = monitor.Evaluate(Wrap(info, MakeUsbFrame(0x01, 0x00)));
            if (ev5.Count != 1 || ev5[0].Percent != 15) c.Failures.Add("after 35% reset, 15% should trigger again");

            return c;
        }

        private static List<DeviceSnapshot> Wrap(DeviceInfo info, byte[] frame)
        {
            int pct;
            bool chg;
            byte s0;
            byte s1;
            string reason;
            bool ok = DualSenseProtocol.TryParseUsbReport(frame, out pct, out chg, out s0, out s1, out reason);

            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            if (ok)
            {
                r.Online = true;
                r.Percent = pct;
                r.Level = BatteryMath.FromPercent(pct);
                r.Charging = chg;
            }
            else
            {
                r.Online = false;
                r.Error = reason;
            }

            DeviceSnapshot snap = new DeviceSnapshot();
            snap.Info = info;
            snap.Reading = r;

            List<DeviceSnapshot> list = new List<DeviceSnapshot>();
            list.Add(snap);
            return list;
        }

        private static HidDeviceInfo MakeHid(ushort vid, ushort pid, ushort up, ushort u, int inLen,
                                             string containerId, string path, string productName)
        {
            HidDeviceInfo h = new HidDeviceInfo();
            h.VendorId = vid;
            h.ProductId = pid;
            h.UsagePage = up;
            h.Usage = u;
            h.InputReportLength = inLen;
            h.OutputReportLength = inLen;
            h.FeatureReportLength = inLen;
            h.ContainerId = containerId;
            h.Path = path;
            h.ProductName = productName;
            return h;
        }

        private static void WriteText(List<CaseResult> cases, bool allPassed, int failed)
        {
            Console.WriteLine("dualsense self test");
            Console.WriteLine("cases: " + cases.Count + "  failed: " + failed);
            for (int i = 0; i < cases.Count; i++)
            {
                CaseResult c = cases[i];
                if (c.Passed)
                {
                    Console.WriteLine("PASS " + c.Id);
                }
                else
                {
                    Console.WriteLine("FAIL " + c.Id + "  " + string.Join("; ", c.Failures.ToArray()));
                }
            }
            Console.WriteLine("RESULT: " + (allPassed ? "PASS" : "FAIL"));
        }

        private static string ToJson(List<CaseResult> cases, bool allPassed, int failed)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"mode\":\"test-dualsense\",\"passed\":").Append(allPassed ? "true" : "false");
            sb.Append(",\"casesCount\":").Append(cases.Count);
            sb.Append(",\"failedCount\":").Append(failed);
            sb.Append(",\"cases\":[");
            for (int i = 0; i < cases.Count; i++)
            {
                if (i != 0) sb.Append(',');
                CaseResult c = cases[i];
                sb.Append("{\"id\":\"").Append(Escape(c.Id)).Append("\",\"passed\":")
                  .Append(c.Passed ? "true" : "false").Append(",\"failures\":[");
                for (int j = 0; j < c.Failures.Count; j++)
                {
                    if (j != 0) sb.Append(',');
                    sb.Append('"').Append(Escape(c.Failures[j])).Append('"');
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private sealed class CaseResult
        {
            public readonly string Id;
            public readonly List<string> Failures = new List<string>();
            public bool Passed { get { return Failures.Count == 0; } }

            public CaseResult(string id)
            {
                Id = id;
            }
        }
    }
}
