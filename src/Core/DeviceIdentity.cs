using System;
using Voltide.Interop;

namespace Voltide.Core
{
    /// <summary>
    /// Physical identity helpers.
    ///
    /// One receiver exposes several HID interfaces (MI_xx / COLxx), each with its
    /// own device path, so a path says which *collection* was opened, not which
    /// physical device is behind it. Windows gives every interface of one physical
    /// device the same container id, which is therefore the preferred key; the
    /// interface instance inside the path is the fallback when Windows exposes no
    /// container id.
    ///
    /// Keys are deliberately built from identity data only (never from VID:PID
    /// alone): two receivers of the same model differ in container/instance and
    /// must stay two rows.
    /// </summary>
    public static class DeviceIdentity
    {
        public const string ContainerPrefix = "container:";
        public const string PathPrefix = "path:";
        public const string DevicePrefix = "id:";

        public static string PhysicalKey(HidDeviceInfo device)
        {
            if (device == null) return "";

            string container = NormalizeContainer(device.ContainerId);
            if (container.Length > 0) return ContainerPrefix + container;

            string pathKey = PathKey(device.Path, device.VendorId, device.ProductId);
            if (pathKey.Length > 0) return PathPrefix + pathKey;

            return "";
        }

        public static string PhysicalKey(DeviceInfo device)
        {
            if (device == null) return "";

            string container = NormalizeContainer(device.ContainerId);
            if (container.Length > 0) return ContainerPrefix + container;

            string pathKey = PathKey(device.HidPath, device.VendorId, device.ProductId);
            if (pathKey.Length > 0) return PathPrefix + pathKey;

            // Devices with no HID interface at all (XInput pads) still need a key;
            // their provider-supplied id is unique within that provider.
            if (!string.IsNullOrEmpty(device.Id)) return DevicePrefix + device.Id;
            return "";
        }

        /// <summary>
        /// The interface instance component of a HID path, e.g. "a&amp;12345678&amp;0&amp;0000"
        /// for "\\?\hid#vid_3554&amp;pid_fa09&amp;mi_01&amp;col01#a&amp;12345678&amp;0&amp;0000#{...}".
        /// Empty for anything that is not a "hid#...#...#" interface path.
        /// </summary>
        public static string InstanceToken(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";

            string p = path.Trim();
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p.Substring(4);

            string[] parts = p.Split('#');
            if (parts.Length < 3) return "";
            if (!parts[0].Equals("hid", StringComparison.OrdinalIgnoreCase)) return "";

            string token = parts[2];
            int slash = token.IndexOf('\\');
            if (slash >= 0) token = token.Substring(0, slash);
            return token.Trim();
        }

        /// <summary>Lower-case "{guid}" form; empty in, empty out.</summary>
        public static string NormalizeContainer(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            string v = value.Trim().ToLowerInvariant();
            if (v.Length == 0) return "";
            if (v[0] != '{') v = "{" + v + "}";
            return v;
        }

        private static string PathKey(string path, ushort vendorId, ushort productId)
        {
            string token = InstanceToken(path);
            if (token.Length == 0) return "";
            return vendorId.ToString("X4") + ":" + productId.ToString("X4") + "#" + token;
        }
    }
}
