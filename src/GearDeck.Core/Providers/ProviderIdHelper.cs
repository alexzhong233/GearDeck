using System.Globalization;

namespace GearDeck.Core.Providers;

/// <summary>
/// 统一解析设备标识符（支持 <c>prefix:VVVV-PPPP</c> 与 <c>prefix:PPPP</c>）。
/// </summary>
public static class ProviderIdHelper
{
    public static bool TryParseVidPid(string? deviceId, out ushort vid, out ushort pid, ushort defaultVid = 0)
    {
        vid = defaultVid;
        pid = 0;
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        int colon = deviceId.IndexOf(':');
        if (colon < 0 || colon + 1 >= deviceId.Length)
            return false;

        string tail = deviceId[(colon + 1)..];
        int dash = tail.IndexOf('-');
        if (dash >= 0)
        {
            return ushort.TryParse(tail.AsSpan(0, dash), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid)
                && ushort.TryParse(tail.AsSpan(dash + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
        }

        return defaultVid != 0
            && ushort.TryParse(tail, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
    }

    public static bool TryParsePid(string? deviceId, out ushort pid)
    {
        pid = 0;
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        int colon = deviceId.IndexOf(':');
        if (colon < 0 || colon + 1 >= deviceId.Length)
            return false;

        string tail = deviceId[(colon + 1)..];
        int dash = tail.IndexOf('-');
        ReadOnlySpan<char> span = dash >= 0 ? tail.AsSpan(dash + 1) : tail.AsSpan();
        return ushort.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
    }
}
