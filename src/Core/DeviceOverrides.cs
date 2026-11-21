using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Voltide.Core
{
    /// <summary>One device-name override, matched on physical identity.</summary>
    public sealed class DeviceOverride
    {
        public string VendorIdHex;
        public string ProductIdHex;
        public string ContainerId;
        public string Instance;
        public string Name;
        public string Brand;
        public string Model;
        public DeviceKind Kind = DeviceKind.Unknown;

        public override string ToString()
        {
            return (Name != null ? Name : "(no name)") + " <- " + VendorIdHex + ":" + ProductIdHex +
                   (ContainerId != null ? " container=" + ContainerId : "") +
                   (Instance != null ? " instance=" + Instance : "");
        }
    }

    /// <summary>
    /// Optional per-device display names, read from "devices.json" next to the exe.
    ///
    /// Why a file instead of a hard-coded mapping: the receiver behind a PID is not
    /// brand-specific (0x3554:0xFA09 is a Compx 2.4G receiver that several vendors
    /// ship — the upstream project maps it to a MCHOSE keyboard). Only the local
    /// user knows that *this* receiver is an HP GT98, and which physical unit it is.
    /// An override therefore has to name one physical device, and the identity
    /// selector is required: an entry without container id and without instance
    /// token never matches, so a typo cannot silently rename every device of a PID.
    ///
    /// The file is optional. A missing file means "no overrides"; a malformed file
    /// also means "no overrides" (fail closed, never guess), with the reason kept in
    /// <see cref="LoadError"/> for diagnostics.
    /// </summary>
    public static class DeviceOverrides
    {
        public const string FileName = "devices.json";

        private static readonly object Gate = new object();
        private static bool _loaded;
        private static List<DeviceOverride> _entries = new List<DeviceOverride>();
        private static string _loadError;
        private static string _sourcePath;

        /// <summary>Full path of the file the overrides were read from (or would be).</summary>
        public static string SourcePath
        {
            get { EnsureLoaded(); return _sourcePath; }
        }

        /// <summary>null when the file was read (or absent); otherwise why it was ignored.</summary>
        public static string LoadError
        {
            get { EnsureLoaded(); return _loadError; }
        }

        public static int Count
        {
            get { EnsureLoaded(); return _entries.Count; }
        }

        public static List<DeviceOverride> Entries
        {
            get { EnsureLoaded(); return new List<DeviceOverride>(_entries); }
        }

        /// <summary>Matches the cached file. Identity selector must be present and equal.</summary>
        public static bool TryMatch(string containerId, string instanceToken, ushort vendorId, ushort productId,
                                    out DeviceOverride match)
        {
            EnsureLoaded();
            match = null;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (Matches(_entries[i], containerId, instanceToken, vendorId, productId))
                {
                    match = _entries[i];
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Rewrites brand/model/name of a discovered device when the local file maps
        /// this exact physical device. Returns true when an override applied.
        /// </summary>
        public static bool Apply(DeviceInfo device, string containerId, string instanceToken)
        {
            if (device == null) return false;

            DeviceOverride match;
            if (!TryMatch(containerId, instanceToken, device.VendorId, device.ProductId, out match)) return false;

            if (!string.IsNullOrEmpty(match.Brand)) device.Brand = match.Brand;
            if (!string.IsNullOrEmpty(match.Model)) device.Model = match.Model;

            if (!string.IsNullOrEmpty(match.Name)) device.Name = match.Name;
            else device.Name = ((device.Brand != null ? device.Brand : "") + " " +
                                (device.Model != null ? device.Model : "")).Trim();

            if (match.Kind != DeviceKind.Unknown) device.Kind = match.Kind;
            if (!string.IsNullOrEmpty(device.Model)) device.ShortModel = device.Model;
            return true;
        }

        /// <summary>
        /// One override matches only when vid/pid are equal *and* the identity
        /// selector (container id, else instance token) is equal. Devices without
        /// that identity never match.
        /// </summary>
        public static bool Matches(DeviceOverride entry, string containerId, string instanceToken,
                                   ushort vendorId, ushort productId)
        {
            if (entry == null) return false;
            if (!string.Equals(entry.VendorIdHex, vendorId.ToString("X4"), StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(entry.ProductIdHex, productId.ToString("X4"), StringComparison.OrdinalIgnoreCase)) return false;

            string container = DeviceIdentity.NormalizeContainer(containerId);
            string instance = instanceToken != null ? instanceToken.Trim() : "";

            if (!string.IsNullOrEmpty(entry.ContainerId))
                return container.Length > 0 && container == entry.ContainerId;

            if (!string.IsNullOrEmpty(entry.Instance))
                return instance.Length > 0 && string.Equals(instance, entry.Instance, StringComparison.OrdinalIgnoreCase);

            // No identity selector: refuse rather than rename by PID alone.
            return false;
        }

        /// <summary>
        /// Parses a overrides file. Used by the provider path on the exe-adjacent
        /// file and by the offline verification entry point on fixture files.
        /// Returns the entries (possibly empty) and the reason they were ignored.
        /// </summary>
        public static List<DeviceOverride> ParseFile(string path, out string error)
        {
            error = null;
            List<DeviceOverride> entries = new List<DeviceOverride>();

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                error = "not found: " + path;
                return entries;
            }

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                error = "read failed: " + ex.Message;
                return entries;
            }

            try
            {
                object root = JsonReader.Parse(text);

                Dictionary<string, object> map = root as Dictionary<string, object>;
                if (map == null)
                {
                    error = "root is not an object";
                    return entries;
                }

                object rawList;
                map.TryGetValue("overrides", out rawList);
                List<object> list = rawList as List<object>;
                if (list == null)
                {
                    error = "\"overrides\" is not an array";
                    return entries;
                }

                int index = 0;
                foreach (object item in list)
                {
                    index++;
                    Dictionary<string, object> node = item as Dictionary<string, object>;
                    if (node == null)
                    {
                        error = "override #" + index + " is not an object";
                        return new List<DeviceOverride>();
                    }

                    DeviceOverride entry = new DeviceOverride();
                    entry.VendorIdHex = NormalizeHex(Text(node, "vid"));
                    entry.ProductIdHex = NormalizeHex(Text(node, "pid"));
                    entry.ContainerId = DeviceIdentity.NormalizeContainer(Text(node, "containerId"));
                    entry.Instance = Text(node, "instance");
                    if (entry.Instance != null) entry.Instance = entry.Instance.Trim();
                    entry.Name = Text(node, "name");
                    entry.Brand = Text(node, "brand");
                    entry.Model = Text(node, "model");
                    entry.Kind = ParseKind(Text(node, "kind"));

                    if (entry.VendorIdHex.Length == 0 || entry.ProductIdHex.Length == 0)
                    {
                        error = "override #" + index + " has no vid/pid";
                        return new List<DeviceOverride>();
                    }

                    entries.Add(entry);
                }
            }
            catch (Exception ex)
            {
                error = "parse failed: " + ex.GetType().Name + ": " + ex.Message;
                return new List<DeviceOverride>();
            }

            return entries;
        }

        private static string Text(Dictionary<string, object> node, string key)
        {
            object value;
            if (node == null || !node.TryGetValue(key, out value) || value == null) return null;
            string text = value as string;
            if (text != null) return text;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string NormalizeHex(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string v = value.Trim();
            if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) v = v.Substring(2);
            int parsed;
            if (!int.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed)) return "";
            return parsed.ToString("X4");
        }

        private static DeviceKind ParseKind(string value)
        {
            if (string.IsNullOrEmpty(value)) return DeviceKind.Unknown;
            string v = value.Trim().ToLowerInvariant();
            switch (v)
            {
                case "mouse": return DeviceKind.Mouse;
                case "keyboard": return DeviceKind.Keyboard;
                case "headset": return DeviceKind.Headset;
                case "other": return DeviceKind.Other;
                default: return DeviceKind.Unknown;
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;

            lock (Gate)
            {
                if (_loaded) return;

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (string.IsNullOrEmpty(baseDir)) baseDir = ".";

                // The file lives next to the exe. The environment variable exists so
                // the offline verification can point the same production loader at a
                // copy without touching the deployed one.
                string path = Environment.GetEnvironmentVariable("VOLTIDE_DEVICES_JSON");
                if (string.IsNullOrEmpty(path)) path = Path.Combine(baseDir, FileName);

                string error;
                List<DeviceOverride> entries = ParseFile(path, out error);

                _sourcePath = path;
                _loadError = error;
                _entries = entries;
                _loaded = true;
            }
        }
    }
}
