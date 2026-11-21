using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// Sony DualSense (054C:0CE6) and DualSense Edge (054C:0DF2) controllers over
    /// standard 64-byte USB HID input report 0x01 — covering both awalol/DS5Dongle
    /// (which bridges Bluetooth report 0x31 to wired USB report 0x01) and direct
    /// wired USB connections.
    ///
    /// Verified on this machine (DS5Dongle v0.7.2, PID 0DF2, UsagePage 0x0001,
    /// Usage 0x0005, InLen/OutLen/FeatLen = 64):
    ///   - Open handle read-only (GENERIC_READ + FILE_SHARE_RW); never writes to
    ///     the controller, so active games, haptics and audio are untouched.
    ///   - HidD_GetInputReport(0x01) returns an all-zero payload here, so readings
    ///     must come from ReadFile on the interrupt-IN pipe.
    ///   - Input report 0x01 (64 bytes = 1-byte report id + 63-byte USBGetStateData):
    ///       buf[0]  = 0x01 (report id)
    ///       buf[53] = status0 (USBGetStateData offset 52):
    ///                 low nibble  = PowerPercent (0x00..0x0A, 10% buckets)
    ///                 high nibble = PowerState (0x0 discharging, 0x1 charging,
    ///                               0x2 complete/full, 0xA/0xB/0xF fault)
    ///       buf[54] = status1 (USBGetStateData offset 53, jack/power/dock bits)
    ///   - Percentage follows Linux hid-playstation / SDL / PlayStation midpoint
    ///     mapping: min(raw * 10 + 5, 100) for states 0x0/0x1, and 100 for 0x2.
    ///   - Read-only Feature Report 0xF8 returns the DS5Dongle firmware version
    ///     string (e.g. "v0.7.2") and is cached per interface path to distinguish
    ///     DS5Dongle from a direct wired USB controller.
    /// </summary>
    public class DualSenseProvider : IDeviceProvider, IClaimPriority
    {
        private static readonly object DetailCacheLock = new object();
        private static readonly Dictionary<string, string> DetailCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Name { get { return "DualSense HID"; } }

        /// <summary>Exact VID + PID + gamepad collection beats generic VID-based claims.</summary>
        public int ClaimStrength { get { return 30; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            if (catalog == null) return found;

            Dictionary<string, HidDeviceInfo> seen =
                new Dictionary<string, HidDeviceInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (HidDeviceInfo hid in catalog)
            {
                if (!DualSenseProtocol.MatchesUsbGamepadInterface(hid)) continue;

                string key = DeviceIdentity.PhysicalKey(hid);
                if (key.Length == 0) key = DeviceIdentity.PathPrefix + hid.Path;
                if (seen.ContainsKey(key)) continue;
                seen.Add(key, hid);

                bool isEdge = DualSenseProtocol.IsEdgeModel(hid.ProductId, hid.ProductName);
                string model = isEdge ? DualSenseProtocol.EdgeModelName : DualSenseProtocol.StandardModelName;
                string shortModel = isEdge ? DualSenseProtocol.EdgeShortModel : DualSenseProtocol.StandardShortModel;

                DeviceInfo device = new DeviceInfo();
                device.Id = "dualsense-" + key;
                device.Brand = "SONY";
                device.Model = model;
                device.ShortModel = shortModel;
                device.Name = "Sony " + model;
                device.Kind = DeviceKind.Other;
                device.VendorId = hid.VendorId;
                device.ProductId = hid.ProductId;
                device.HidPath = hid.Path;
                device.ContainerId = hid.ContainerId;
                device.ProviderName = Name;

                DeviceOverrides.Apply(device, hid.ContainerId, DeviceIdentity.InstanceToken(hid.Path));

                found.Add(device);
            }

            return found;
        }

        public DeviceReading Read(DeviceInfo device)
        {
            DeviceReading reading = new DeviceReading();
            reading.UpdatedAt = DateTime.Now;
            reading.Level = BatteryLevel.Unknown;
            reading.Online = false;

            if (device == null ||
                device.VendorId != DualSenseProtocol.VendorId ||
                !DualSenseProtocol.IsSupportedProductId(device.ProductId) ||
                string.IsNullOrEmpty(device.HidPath))
            {
                reading.Error = "不支持的 DualSense 设备";
                return reading;
            }

            try
            {
                using (HidSession session = new HidSession(
                    device.HidPath,
                    DualSenseProtocol.UsbInputReportLength,
                    DualSenseProtocol.UsbInputReportLength,
                    true))
                {
                    byte[] frame = ReadUsbStateFrame(session, DualSenseProtocol.ReadTimeoutMs);
                    if (frame == null)
                    {
                        reading.Sleeping = true;
                        reading.Error = "无输入报告（设备休眠或未连接）";
                        return reading;
                    }

                    reading.Raw = Hex(frame);

                    int percent;
                    bool charging;
                    byte rawStatus0;
                    byte rawStatus1;
                    string reason;
                    if (!DualSenseProtocol.TryParseUsbReport(
                        frame, out percent, out charging, out rawStatus0, out rawStatus1, out reason))
                    {
                        reading.Error = reason != null ? reason : "DualSense 电量报告无效";
                        return reading;
                    }

                    reading.Percent = percent;
                    reading.Level = BatteryMath.FromPercent(percent);
                    reading.Charging = charging;
                    reading.ChargingKnown = true;
                    reading.Online = true;
                    reading.Detail = ResolveDetail(session, device.HidPath);
                }
            }
            catch (Exception ex)
            {
                reading.Online = false;
                reading.Error = DescribeOpenFailure(ex);
            }

            return reading;
        }

        private static byte[] ReadUsbStateFrame(HidSession session, int totalTimeoutMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            for (int attempt = 0; attempt < DualSenseProtocol.MaxReadFrames; attempt++)
            {
                int remaining = totalTimeoutMs - (int)clock.ElapsedMilliseconds;
                if (remaining <= 0) break;

                byte[] buf = session.Read(remaining);
                if (buf == null) break;

                if (buf.Length >= DualSenseProtocol.MinReportBytesForBattery &&
                    buf[0] == DualSenseProtocol.UsbInputReportId)
                {
                    return buf;
                }
            }
            return null;
        }

        private static string ResolveDetail(HidSession session, string hidPath)
        {
            if (!string.IsNullOrEmpty(hidPath))
            {
                lock (DetailCacheLock)
                {
                    string cached;
                    if (DetailCache.TryGetValue(hidPath, out cached)) return cached;
                }
            }

            string detail = DualSenseProtocol.DetailUsb;
            try
            {
                byte[] feature = new byte[DualSenseProtocol.UsbInputReportLength];
                feature[0] = DualSenseProtocol.DongleVersionFeatureId;
                if (HidNative.HidD_GetFeature(session.Handle, feature, feature.Length))
                {
                    string version;
                    if (DualSenseProtocol.TryParseDongleVersion(feature, out version))
                    {
                        detail = DualSenseProtocol.DetailDongle;
                    }
                }
            }
            catch
            {
                detail = DualSenseProtocol.DetailUsb;
            }

            if (!string.IsNullOrEmpty(hidPath))
            {
                lock (DetailCacheLock)
                {
                    DetailCache[hidPath] = detail;
                }
            }

            return detail;
        }

        private static string DescribeOpenFailure(Exception ex)
        {
            string message = ex != null ? ex.Message : "";
            if (message != null && message.IndexOf("Cannot open HID device", StringComparison.OrdinalIgnoreCase) >= 0)
                return "设备已断开（无法打开 HID 接口）";
            return message != null && message.Length > 0 ? message : "读取失败";
        }

        private static string Hex(byte[] data)
        {
            if (data == null) return "";
            StringBuilder text = new StringBuilder(data.Length * 3);
            for (int i = 0; i < data.Length; i++)
            {
                if (i != 0) text.Append(' ');
                text.Append(data[i].ToString("X2"));
            }
            return text.ToString();
        }
    }

    /// <summary>
    /// Pure protocol helpers for DualSense / DualSense Edge USB HID report 0x01
    /// (and DS5Dongle identification). Free of I/O so offline self-tests execute
    /// the exact production parser.
    /// </summary>
    public static class DualSenseProtocol
    {
        public const ushort VendorId = 0x054C;
        public const ushort ProductIdDualSense = 0x0CE6;
        public const ushort ProductIdDualSenseEdge = 0x0DF2;

        public const ushort UsagePageGenericDesktop = 0x0001;
        public const ushort UsageGamePad = 0x0005;

        public const int UsbInputReportLength = 64;
        public const byte UsbInputReportId = 0x01;
        public const int BatteryByteOffset = 53;
        public const int Status1ByteOffset = 54;
        public const int MinReportBytesForBattery = 55;

        public const int PowerStateDischarging = 0x00;
        public const int PowerStateCharging = 0x01;
        public const int PowerStateComplete = 0x02;
        public const int PowerStateAbnormalVoltage = 0x0A;
        public const int PowerStateAbnormalTemperature = 0x0B;
        public const int PowerStateChargingError = 0x0F;

        public const int MaxRawBatteryLevel = 0x0A;

        public const byte DongleVersionFeatureId = 0xF8;
        public const int ReadTimeoutMs = 600;
        public const int MaxReadFrames = 8;

        public const string StandardModelName = "DualSense";
        public const string StandardShortModel = "DualSense";
        public const string EdgeModelName = "DualSense Edge";
        public const string EdgeShortModel = "DS5 Edge";

        public const string DetailDongle = "DS5Dongle";
        public const string DetailUsb = "USB";

        public static bool IsSupportedProductId(ushort productId)
        {
            return productId == ProductIdDualSense || productId == ProductIdDualSenseEdge;
        }

        public static bool IsEdgeModel(ushort productId, string productName)
        {
            if (productId == ProductIdDualSenseEdge) return true;
            if (!string.IsNullOrEmpty(productName) &&
                productName.IndexOf("Edge", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            return false;
        }

        public static bool MatchesUsbGamepadInterface(HidDeviceInfo hid)
        {
            return hid != null &&
                hid.VendorId == VendorId &&
                IsSupportedProductId(hid.ProductId) &&
                hid.UsagePage == UsagePageGenericDesktop &&
                hid.Usage == UsageGamePad &&
                hid.InputReportLength == UsbInputReportLength &&
                !string.IsNullOrEmpty(hid.Path);
        }

        /// <summary>
        /// Decodes DualSense USB input report 0x01 battery status.
        /// Uses the official PlayStation / Linux hid-playstation / SDL midpoint
        /// mapping: min(rawLevel * 10 + 5, 100) for states 0x0 and 0x1, and 100%
        /// for state 0x2 (charging complete). Rejects out-of-range raw levels
        /// (&gt; 0x0A, e.g. 0x0C no-battery sentinel) and fault power states without
        /// clamping or guessing.
        /// </summary>
        public static bool TryParseUsbReport(
            byte[] report,
            out int percent,
            out bool charging,
            out byte rawStatus0,
            out byte rawStatus1,
            out string reason)
        {
            percent = 0;
            charging = false;
            rawStatus0 = 0;
            rawStatus1 = 0;
            reason = null;

            if (report == null || report.Length < MinReportBytesForBattery)
            {
                reason = "输入报告过短（少于 " + MinReportBytesForBattery + " 字节）";
                return false;
            }

            if (report[0] != UsbInputReportId)
            {
                reason = "报告 ID 不匹配（期望 0x01，实际 0x" + report[0].ToString("X2") + "）";
                return false;
            }

            rawStatus0 = report[BatteryByteOffset];
            rawStatus1 = report[Status1ByteOffset];

            int rawLevel = rawStatus0 & 0x0F;
            int powerState = (rawStatus0 >> 4) & 0x0F;

            if (powerState == PowerStateDischarging)
            {
                if (rawLevel > MaxRawBatteryLevel)
                {
                    reason = "放电电量档位无效（raw=0x" + rawLevel.ToString("X") + "，有效范围 0x0-0xA）";
                    return false;
                }
                percent = MidpointPercent(rawLevel);
                charging = false;
                return true;
            }

            if (powerState == PowerStateCharging)
            {
                if (rawLevel > MaxRawBatteryLevel)
                {
                    reason = "充电电量档位无效（raw=0x" + rawLevel.ToString("X") + "，有效范围 0x0-0xA）";
                    return false;
                }
                percent = MidpointPercent(rawLevel);
                charging = true;
                return true;
            }

            if (powerState == PowerStateComplete)
            {
                percent = 100;
                charging = true;
                return true;
            }

            if (powerState == PowerStateAbnormalVoltage)
            {
                reason = "电源状态异常：电压异常（0x0A）";
                return false;
            }

            if (powerState == PowerStateAbnormalTemperature)
            {
                reason = "电源状态异常：温度异常（0x0B）";
                return false;
            }

            if (powerState == PowerStateChargingError)
            {
                reason = "电源状态异常：充电故障（0x0F）";
                return false;
            }

            reason = "未知电源状态（state=0x" + powerState.ToString("X") + ", raw=0x" + rawStatus0.ToString("X2") + "）";
            return false;
        }

        public static int MidpointPercent(int rawLevel)
        {
            int value = rawLevel * 10 + 5;
            return value > 100 ? 100 : value;
        }

        /// <summary>
        /// Parses the ASCII firmware version string returned by awalol/DS5Dongle
        /// Feature Report 0xF8 (e.g. "v0.7.2").
        /// </summary>
        public static bool TryParseDongleVersion(byte[] featureBuffer, out string version)
        {
            version = "";
            if (featureBuffer == null || featureBuffer.Length < 2) return false;
            if (featureBuffer[0] != DongleVersionFeatureId) return false;

            StringBuilder sb = new StringBuilder();
            bool hasDigit = false;
            for (int i = 1; i < featureBuffer.Length; i++)
            {
                byte b = featureBuffer[i];
                if (b == 0) break;
                if (b < 0x20 || b > 0x7E) return false;
                char c = (char)b;
                if (c >= '0' && c <= '9') hasDigit = true;
                sb.Append(c);
            }

            string text = sb.ToString().Trim();
            if (text.Length < 2 || !hasDigit) return false;

            version = text;
            return true;
        }
    }
}
