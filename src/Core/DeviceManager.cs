using System;
using System.Collections.Generic;
using System.Threading;
using Voltide.Interop;
using Voltide.Providers;

namespace Voltide.Core
{
    public class DeviceSnapshot
    {
        public DeviceInfo Info;
        public DeviceReading Reading;
    }

    /// <summary>
    /// Owns all protocol providers and produces a stable snapshot for the UI.
    /// Refreshing happens on a background thread; the UI only ever reads snapshots.
    /// </summary>
    public class DeviceManager
    {
        private readonly List<IDeviceProvider> _providers = new List<IDeviceProvider>();
        private readonly object _lock = new object();
        private readonly object _refreshLock = new object();
        private List<DeviceSnapshot> _snapshots = new List<DeviceSnapshot>();
        private List<string> _claimDiagnostics = new List<string>();

        public DeviceManager()
        {
            _providers.Add(new LogitechProvider());
            _providers.Add(new MchoseProvider());
            _providers.Add(new MchoseK7MouseProvider());
            _providers.Add(new MchoseA5MouseProvider());
            _providers.Add(new RapooDeviceProvider());
            _providers.Add(new AsusRogProvider());
            _providers.Add(new AsusHeadsetProvider());
            _providers.Add(new TaiduProvider());
            _providers.Add(new ImecooProvider());
            _providers.Add(new AtkMouseProvider());
            _providers.Add(new AtkHeadsetProvider());
            _providers.Add(new XInputProvider());
            _providers.Add(new DualSenseProvider());
            _providers.Add(new RazerProvider());
            _providers.Add(new HeadsetProvider());
        }

        /// <summary>Provider names in registration order (diagnostics / verification).</summary>
        public List<string> ProviderNames()
        {
            List<string> names = new List<string>();
            foreach (IDeviceProvider p in _providers) names.Add(p.Name);
            return names;
        }

        /// <summary>
        /// Conflicts the claim resolver had to settle on the last refresh; empty when
        /// every physical device was claimed by exactly one provider.
        /// </summary>
        public List<string> ClaimDiagnostics()
        {
            lock (_lock)
            {
                return new List<string>(_claimDiagnostics);
            }
        }

        public List<DeviceSnapshot> Snapshot()
        {
            lock (_lock)
            {
                return new List<DeviceSnapshot>(_snapshots);
            }
        }

        /// <summary>Blocking. Safe to call from several threads: calls are serialized.</summary>
        public void Refresh()
        {
            lock (_refreshLock)
            {
                RefreshCore();
            }
        }

        private void RefreshCore()
        {
            // Enumerate once per refresh and share the result. Every provider used to
            // walk the whole HID tree itself, which cost ~400 ms per provider on a
            // machine with a busy USB stack — five providers meant two seconds of
            // identical work.
            List<HidDeviceInfo> catalog;
            try
            {
                catalog = HidNative.Enumerate();
            }
            catch
            {
                catalog = new List<HidDeviceInfo>();
            }

            // Discover first, resolve ownership second, read only the owners. A
            // duplicate is dropped before any protocol traffic is sent to it.
            List<DeviceClaim> claims = new List<DeviceClaim>();
            foreach (IDeviceProvider p in _providers)
            {
                List<DeviceInfo> devices;
                try
                {
                    devices = p.Discover(catalog);
                }
                catch
                {
                    continue;
                }
                if (devices == null) continue;

                IClaimPriority priority = p as IClaimPriority;
                int strength = priority != null ? priority.ClaimStrength : DeviceClaimResolver.DefaultStrength;

                foreach (DeviceInfo d in devices)
                {
                    if (d == null) continue;
                    DeviceClaim claim = new DeviceClaim();
                    claim.Provider = p;
                    claim.Device = d;
                    claim.Strength = strength;
                    claims.Add(claim);
                }
            }

            List<string> conflicts;
            List<DeviceClaim> owners = DeviceClaimResolver.Resolve(claims, out conflicts);

            List<DeviceSnapshot> next = new List<DeviceSnapshot>();
            foreach (DeviceClaim claim in owners)
            {
                DeviceReading r;
                try
                {
                    r = claim.Provider.Read(claim.Device);
                }
                catch (Exception ex)
                {
                    r = new DeviceReading();
                    r.UpdatedAt = DateTime.Now;
                    r.Error = ex.Message;
                    r.Level = BatteryLevel.Unknown;
                }

                DeviceSnapshot s = new DeviceSnapshot();
                s.Info = claim.Device;
                s.Reading = r;
                next.Add(s);
            }

            lock (_lock)
            {
                _snapshots = next;
                _claimDiagnostics = conflicts;
            }
        }
    }
}
