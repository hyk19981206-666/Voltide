using System;
using System.Collections.Generic;
using Voltide.Providers;

namespace Voltide.Core
{
    /// <summary>One provider's claim on one discovered device.</summary>
    public sealed class DeviceClaim
    {
        public IDeviceProvider Provider;
        public DeviceInfo Device;
        public int Strength;
    }

    /// <summary>
    /// Decides which claims survive when several providers claim the same physical
    /// device, without depending on provider registration order.
    ///
    /// Rules
    ///   - claims from the same provider are kept together: collapsing a provider's
    ///     own output is the provider's business (a headset may expose two rows).
    ///   - claims from different providers on one physical key are a conflict. The
    ///     winner is the provider with the larger <see cref="IClaimPriority"/> number,
    ///     ties broken by provider name (ordinal), then by device id — all inputs, so
    ///     reversing the registration order cannot change the result.
    ///   - every conflict is reported, never silently dropped.
    /// </summary>
    public static class DeviceClaimResolver
    {
        /// <summary>Strength of providers that do not declare one.</summary>
        public const int DefaultStrength = 0;

        public static List<DeviceClaim> Resolve(List<DeviceClaim> claims, out List<string> conflicts)
        {
            List<DeviceClaim> kept = new List<DeviceClaim>();
            conflicts = new List<string>();
            if (claims == null) return kept;

            Dictionary<string, List<DeviceClaim>> groups =
                new Dictionary<string, List<DeviceClaim>>(StringComparer.OrdinalIgnoreCase);
            List<string> order = new List<string>();

            foreach (DeviceClaim claim in claims)
            {
                if (claim == null || claim.Device == null) continue;

                string key = DeviceIdentity.PhysicalKey(claim.Device);
                if (key.Length == 0) key = DeviceIdentity.DevicePrefix + (claim.Device.Id != null ? claim.Device.Id : "");

                List<DeviceClaim> bucket;
                if (!groups.TryGetValue(key, out bucket))
                {
                    bucket = new List<DeviceClaim>();
                    groups.Add(key, bucket);
                    order.Add(key);
                }
                bucket.Add(claim);
            }

            foreach (string key in order)
            {
                List<DeviceClaim> bucket = groups[key];

                List<string> providers = new List<string>();
                foreach (DeviceClaim claim in bucket)
                {
                    string name = ProviderName(claim);
                    if (!providers.Contains(name)) providers.Add(name);
                }

                if (providers.Count <= 1)
                {
                    kept.AddRange(bucket);
                    continue;
                }

                DeviceClaim winner = bucket[0];
                foreach (DeviceClaim claim in bucket)
                {
                    if (Compare(claim, winner) < 0) winner = claim;
                }

                string winnerName = ProviderName(winner);
                foreach (DeviceClaim claim in bucket)
                {
                    if (string.Equals(ProviderName(claim), winnerName, StringComparison.Ordinal)) kept.Add(claim);
                }

                providers.Sort(StringComparer.Ordinal);
                conflicts.Add("claim-conflict " + key + " providers=[" + string.Join(", ", providers.ToArray()) +
                              "] winner=" + winnerName + " strength=" + winner.Strength);
            }

            return kept;
        }

        /// <summary>Negative when <paramref name="a"/> beats <paramref name="b"/>.</summary>
        public static int Compare(DeviceClaim a, DeviceClaim b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return 1;
            if (b == null) return -1;

            if (a.Strength != b.Strength) return a.Strength > b.Strength ? -1 : 1;

            int byName = string.CompareOrdinal(ProviderName(a), ProviderName(b));
            if (byName != 0) return byName;

            return string.CompareOrdinal(a.Device != null ? a.Device.Id : null,
                                         b.Device != null ? b.Device.Id : null);
        }

        private static string ProviderName(DeviceClaim claim)
        {
            if (claim == null || claim.Provider == null) return "";
            string name = claim.Provider.Name;
            return name != null ? name : "";
        }
    }
}
