using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// Rapoo 2.4G mice.
    ///
    /// Only the interface verified on this machine is claimed: VID 0x24AE,
    /// PID 0x14A5 (VT3 PRO V2 receiver), vendor collection 0xFF00:0x000E with
    /// 33-byte reports. The protocol itself is public (OpenMouse-Project
    /// mouse-protocol, AGPL-3.0) and was re-measured here in card 04C:
    ///
    ///   TX  HidD_SetOutputReport, buffer = caps.OutputReportByteLength (33):
    ///       [0]=0x06 (report id measured on this unit; the doc's 0xBA is refused
    ///                 with Win32 87 here), [1]=0xA5 connection, [2]=0xAA battery
    ///                 command, [3]=0x00 length, [4..7]=address 0, rest 0.
    ///   RX  HidD_GetInputReport(reportId=6), 33 bytes, report id stripped:
    ///       [0]=status (0x01 answered, anything else busy),
    ///       [1]=marker (1 battery, 2 charging), [2]=percent (reject > 100).
    ///
    /// The link drops ~40% of frames, and an OK that was not preceded by a busy
    /// status is the *previous* command's answer. The read therefore retries a
    /// few times and only accepts a battery value after a busy -> 0x01 transition;
    /// anything else leaves the reading unknown instead of showing a stale number.
    /// </summary>
    public class RapooDeviceProvider : IDeviceProvider, IClaimPriority
    {
        public string Name { get { return "Rapoo HID"; } }

        /// <summary>Exact PID + interface fingerprint beats generic VID-based claims.</summary>
        public int ClaimStrength { get { return 30; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            if (catalog == null) return found;

            Dictionary<string, HidDeviceInfo> seen =
                new Dictionary<string, HidDeviceInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (HidDeviceInfo hid in catalog)
            {
                if (!RapooProtocol.MatchesConfigInterface(hid)) continue;

                string key = DeviceIdentity.PhysicalKey(hid);
                if (key.Length == 0) key = DeviceIdentity.PathPrefix + hid.Path;
                if (seen.ContainsKey(key)) continue;
                seen.Add(key, hid);

                DeviceInfo device = new DeviceInfo();
                device.Id = "rapoo-" + key;
                device.Brand = "Rapoo";
                device.Model = RapooProtocol.ModelName;
                device.Name = "Rapoo " + RapooProtocol.ModelName;
                device.ShortModel = RapooProtocol.ModelName;
                device.Kind = DeviceKind.Mouse;
                device.VendorId = hid.VendorId;
                device.ProductId = hid.ProductId;
                device.HidPath = hid.Path;
                device.ContainerId = hid.ContainerId;
                device.ProviderName = Name;

                // Local display-name override, matched on physical identity only.
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
                device.VendorId != RapooProtocol.VendorId ||
                device.ProductId != RapooProtocol.VerifiedProductId ||
                string.IsNullOrEmpty(device.HidPath))
            {
                reading.Error = "不支持的雷柏设备";
                return reading;
            }

            try
            {
                byte[] query = RapooProtocol.BuildBatteryQuery();

                using (HidSession session = new HidSession(device.HidPath, RapooProtocol.ReportLength, RapooProtocol.ReportLength))
                {
                    session.Flush(20);

                    byte[] accepted = null;
                    byte[] lastReply = null;
                    byte[] unconfirmed = null;          // valid battery bytes without a busy handshake: stale
                    bool sawAnswer = false;
                    bool sawBusy = false;
                    string reason = null;
                    int percent = 0;
                    bool charging = false;

                    for (int attempt = 1; attempt <= RapooProtocol.TxAttempts && accepted == null; attempt++)
                    {
                        if (attempt > 1) Thread.Sleep(RapooProtocol.PollIntervalMs);

                        if (!session.SetOutputReport(query))
                        {
                            reading.Error = "发送电量查询失败（设备被占用？）";
                            return reading;
                        }

                        // The busy status belongs to this exchange only, so the
                        // handshake state restarts with every query frame.
                        sawBusy = false;

                        for (int poll = 0; poll < RapooProtocol.PollAttempts && accepted == null; poll++)
                        {
                            if (poll > 0) Thread.Sleep(RapooProtocol.PollIntervalMs);

                            byte[] reply = new byte[RapooProtocol.ReportLength];
                            reply[0] = RapooProtocol.ReportId;
                            if (!session.GetInputReport(reply)) continue;   // no report available this poll

                            sawAnswer = true;
                            lastReply = reply;

                            RapooReplyKind kind = RapooProtocol.Classify(reply, out percent, out charging, out reason);

                            if (kind == RapooReplyKind.Busy)
                            {
                                sawBusy = true;
                                continue;
                            }

                            if (kind == RapooReplyKind.Battery)
                            {
                                if (!sawBusy)
                                {
                                    // An OK that was not preceded by a busy is the previous
                                    // command's answer; keep it only as evidence, never as
                                    // the current reading.
                                    unconfirmed = reply;
                                    reason = "应答未伴随 busy 状态，判定为旧报文";
                                    continue;
                                }
                                accepted = reply;
                                break;
                            }

                            // Malformed / wrong-kind answer. After a busy handshake it is
                            // this query's answer and must be rejected outright.
                            if (sawBusy)
                            {
                                reading.Raw = Hex(reply);
                                reading.Error = reason != null ? reason : "电量回复无效";
                                return reading;
                            }
                            unconfirmed = reply;
                        }
                    }

                    if (accepted == null)
                    {
                        byte[] shown = lastReply != null ? lastReply : unconfirmed;
                        if (shown != null) reading.Raw = Hex(shown);

                        if (!sawAnswer)
                        {
                            // Nothing came back at all: the dongle is there but the
                            // mouse is asleep / out of range.
                            reading.Sleeping = true;
                            reading.Error = "无应答（设备休眠或未连接）";
                        }
                        else
                        {
                            reading.Error = "无有效电量应答：" + (reason != null ? reason : "未完成 busy→0x01 握手");
                        }
                        return reading;
                    }

                    reading.Raw = Hex(accepted);
                    reading.Percent = percent;
                    reading.Level = BatteryMath.FromPercent(percent);
                    reading.Charging = charging;
                    reading.ChargingKnown = true;
                    reading.Online = true;
                    reading.Detail = "2.4G";
                }
            }
            catch (Exception ex)
            {
                reading.Online = false;
                reading.Error = DescribeOpenFailure(ex);
            }

            return reading;
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

    internal enum RapooReplyKind
    {
        /// <summary>Status byte is not 0x01: the device is still working on a frame.</summary>
        Busy,
        /// <summary>Answered frame whose marker byte is 0: a register block, not a battery value.</summary>
        BlockAnswer,
        /// <summary>Answered battery/charging report with a valid percentage.</summary>
        Battery,
        /// <summary>Answered with a percentage above 100: rejected, never clamped.</summary>
        InvalidPercent,
        /// <summary>Answered with a marker neither 0, 1 nor 2: rejected.</summary>
        InvalidMarker,
        /// <summary>Shorter than the three bytes a battery answer needs.</summary>
        InvalidFrame
    }

    /// <summary>
    /// Encoding/decoding of the Rapoo 0xAA battery exchange, kept free of transport
    /// so the parser is the exact code the provider runs (and the code the offline
    /// verification calls).
    /// </summary>
    internal static class RapooProtocol
    {
        internal const ushort VendorId = 0x24AE;

        /// <summary>The only PID verified on this machine (VT3 PRO V2 2.4G receiver).</summary>
        internal const ushort VerifiedProductId = 0x14A5;

        internal const ushort ConfigUsagePage = 0xFF00;
        internal const ushort ConfigUsage = 0x000E;
        internal const int ReportLength = 33;

        /// <summary>Measured report id of the config/command channel on this unit.</summary>
        internal const byte ReportId = 0x06;

        internal const byte ConnectionReceiver = 0xA5;
        internal const byte CommandBattery = 0xAA;
        internal const byte StatusOk = 0x01;
        internal const byte MarkerBattery = 0x01;
        internal const byte MarkerCharging = 0x02;

        internal const int TxAttempts = 2;
        internal const int PollAttempts = 5;
        internal const int PollIntervalMs = 80;

        /// <summary>
        /// AHUB calls this PID "VT3 PRO V2" (card 04 transcription). The value is a
        /// constant here, not read from AHUB's configuration at runtime.
        /// </summary>
        internal const string ModelName = "VT3 PRO V2";

        internal static bool MatchesConfigInterface(HidDeviceInfo hid)
        {
            return hid != null &&
                hid.VendorId == VendorId &&
                hid.ProductId == VerifiedProductId &&
                hid.UsagePage == ConfigUsagePage &&
                hid.Usage == ConfigUsage &&
                hid.InputReportLength == ReportLength &&
                hid.OutputReportLength == ReportLength &&
                !string.IsNullOrEmpty(hid.Path);
        }

        /// <summary>
        /// 33-byte output buffer: report id, then the 31-byte frame
        /// "A5 AA 00 + address 0 + length 0".
        /// </summary>
        internal static byte[] BuildBatteryQuery()
        {
            byte[] buffer = new byte[ReportLength];
            buffer[0] = ReportId;
            buffer[1] = ConnectionReceiver;
            buffer[2] = CommandBattery;
            buffer[3] = 0x00;                                   // payload length
            buffer[4] = 0x00;
            buffer[5] = 0x00;
            buffer[6] = 0x00;
            buffer[7] = 0x00;                                   // address 0x00000000
            return buffer;
        }

        /// <summary>
        /// Classifies one GET_REPORT(Input) buffer. Accepts the report id in front
        /// only when it equals ours (a transport may keep it); otherwise the reply
        /// starts with the status byte.
        /// </summary>
        internal static RapooReplyKind Classify(byte[] reply, out int percent, out bool charging, out string reason)
        {
            percent = 0;
            charging = false;
            reason = null;

            if (reply == null || reply.Length < 3)
            {
                reason = "应答过短（少于 3 字节）";
                return RapooReplyKind.InvalidFrame;
            }

            int offset = (reply.Length >= ReportLength && reply[0] == ReportId) ? 1 : 0;
            if (reply.Length - offset < 3)
            {
                reason = "应答过短（少于 3 字节）";
                return RapooReplyKind.InvalidFrame;
            }

            byte status = reply[offset];
            if (status != StatusOk)
            {
                reason = "busy（status=0x" + status.ToString("X2") + "）";
                return RapooReplyKind.Busy;
            }

            byte marker = reply[offset + 1];
            if (marker == 0x00)
            {
                reason = "块应答（marker=0x00），不是电量";
                return RapooReplyKind.BlockAnswer;
            }

            if (marker != MarkerBattery && marker != MarkerCharging)
            {
                reason = "未知标记（marker=0x" + marker.ToString("X2") + "）";
                return RapooReplyKind.InvalidMarker;
            }

            int value = reply[offset + 2];
            if (value > 100)
            {
                reason = "电量字节越界（" + value + " > 100，不 clamp）";
                return RapooReplyKind.InvalidPercent;
            }

            percent = value;
            charging = marker == MarkerCharging;
            return RapooReplyKind.Battery;
        }
    }
}
