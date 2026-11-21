namespace Voltide.Providers
{
    /// <summary>
    /// Optional statement of how strongly a provider owns the devices it claims.
    ///
    /// Two providers must never both list the same physical device. Today no pair
    /// overlaps, but that is a property of the current device set, not of the code:
    /// the moment two claims collide, the outcome must be decided by an explicit
    /// number, not by which provider happens to be registered first. Providers that
    /// do not implement this interface count as
    /// <see cref="Voltide.Core.DeviceClaimResolver.DefaultStrength"/>.
    /// </summary>
    public interface IClaimPriority
    {
        /// <summary>Larger wins when two providers claim one physical device.</summary>
        int ClaimStrength { get; }
    }
}
