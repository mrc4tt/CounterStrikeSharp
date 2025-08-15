using System.Runtime.InteropServices;

namespace CounterStrikeSharp.API.Core;

public partial class CTakeDamageInfo
{
    /// <summary>
    /// Retrieves the hitgroup
    /// </summary>
    /// <returns>
    /// Returns a <see cref="HitGroup_t"/> enumeration representing the player's current hit group,
    /// or <see cref="HitGroup_t.HITGROUP_INVALID"/> if the hit group cannot be determined.
    /// </returns>
    public HitGroup_t GetHitGroup()
    {
        if (Handle == IntPtr.Zero)
            return HitGroup_t.HITGROUP_INVALID;

        try
        {
            byte hitGroupValue = Marshal.ReadByte(Handle, 0x80);
            return (HitGroup_t)hitGroupValue;
        }
        catch
        {
            return HitGroup_t.HITGROUP_INVALID;
        }
    }
}
