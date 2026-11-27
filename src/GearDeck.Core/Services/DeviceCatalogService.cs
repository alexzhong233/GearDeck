using System.Globalization;
using System.Text;
using System.Text.Json;
using GearDeck.Core.Models;

namespace GearDeck.Core.Services;

public sealed class DeviceCatalogEntry
{
    public string Vid { get; set; } = "";
    public string Pid { get; set; } = "";
    public string Brand { get; set; } = "";
    public string Model { get; set; } = "";
    public string Kind { get; set; } = "Mouse";
    public string Transport { get; set; } = "2.4G";
    public string Protocol { get; set; } = "";

    public DeviceKind ParsedKind => Kind?.Trim().ToLowerInvariant() switch
    {
        "keyboard" => DeviceKind.Keyboard,
        "headset" => DeviceKind.Headset,
        "controller" => DeviceKind.Controller,
        _ => DeviceKind.Mouse,
    };
}

public sealed class DeviceCatalogManifest
{
    public int Version { get; set; } = 1;
    public List<DeviceCatalogEntry> Entries { get; set; } = [];
}

/// <summary>
/// 外置 VID:PID 设备目录服务：优先合并用户本地热更新目录，支持无需重编扩展新外设型号识别。
/// </summary>
public static class DeviceCatalogService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    private static readonly object Gate = new();
    private static Dictionary<(ushort Vid, ushort Pid), DeviceCatalogEntry>? _lookup;
    private static List<DeviceCatalogEntry>? _entries;
    private static DeviceCatalogManifest? _testOverride;

    private static string BundledCatalogPath => Path.Combine(AppContext.BaseDirectory, "Assets", "device-catalog.json");

    private static string UserCatalogPath
    {
        get
        {
            string dir = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(dir, "GearDeck", "device-catalog.json");
        }
    }

    public static DeviceCatalogManifest ParseManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new DeviceCatalogManifest();

        try
        {
            var manifest = JsonSerializer.Deserialize<DeviceCatalogManifest>(json, JsonOpts) ?? new DeviceCatalogManifest();
            manifest.Entries ??= [];
            return manifest;
        }
        catch
        {
            return new DeviceCatalogManifest();
        }
    }

    public static bool TryLookup(ushort vid, ushort pid, out DeviceCatalogEntry? entry)
    {
        EnsureLoaded();
        lock (Gate)
            return _lookup!.TryGetValue((vid, pid), out entry);
    }

    public static IReadOnlyList<DeviceCatalogEntry> GetByProtocol(string protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol)) return [];
        EnsureLoaded();
        lock (Gate)
        {
            return _entries!
                .Where(e => string.Equals(e.Protocol, protocol, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    public static IReadOnlyList<DeviceCatalogEntry> GetAllEntries()
    {
        EnsureLoaded();
        lock (Gate)
            return _entries!.ToList();
    }

    public static void Reload()
    {
        lock (Gate)
        {
            _lookup = null;
            _entries = null;
        }
    }

    public static void SeedForTest(DeviceCatalogManifest? manifest)
    {
        lock (Gate)
        {
            _testOverride = manifest;
            _lookup = null;
            _entries = null;
        }
    }

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_lookup != null && _entries != null)
                return;

            var merged = new Dictionary<(ushort Vid, ushort Pid), DeviceCatalogEntry>();
            if (_testOverride != null)
            {
                AddEntries(merged, _testOverride.Entries);
            }
            else
            {
                if (File.Exists(BundledCatalogPath))
                    AddEntries(merged, ParseManifest(TryRead(BundledCatalogPath)).Entries);
                if (File.Exists(UserCatalogPath))
                    AddEntries(merged, ParseManifest(TryRead(UserCatalogPath)).Entries);
            }

            _lookup = merged;
            _entries = merged.Values.ToList();
        }
    }

    private static void AddEntries(Dictionary<(ushort Vid, ushort Pid), DeviceCatalogEntry> target, IEnumerable<DeviceCatalogEntry> items)
    {
        foreach (var e in items)
        {
            if (TryParseHex(e.Vid, out ushort v) && TryParseHex(e.Pid, out ushort p))
                target[(v, p)] = e;
        }
    }

    private static bool TryParseHex(string? raw, out ushort value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string clean = raw.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        return ushort.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static string? TryRead(string path)
    {
        try { return File.ReadAllText(path, Encoding.UTF8); }
        catch { return null; }
    }
}
