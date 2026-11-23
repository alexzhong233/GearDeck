using GearDeck.Core;
using GearDeck.Core.Models;
using GearDeck.Core.Providers;
using GearDeck.Core.Services;
using Xunit;

namespace GearDeck.Tests;

public class DeviceUnifierTests
{
    [Fact]
    public void Unify_LogitechWiredChargingAndSleepingReceiver_MergesIntoCanonicalWirelessCardAndRoutesTuning()
    {
        var raw = new List<PeripheralDevice>
        {
            new()
            {
                Id = "logi:C54F:01",
                VendorId = 0x046D,
                ProductId = 0xC54F,
                Name = "Logitech PRO X3 SUPERSTRIKE",
                Kind = DeviceKind.Mouse,
                Provider = "Logitech",
                Connected = true,
                IsSleeping = true,
                Transport = "2.4G",
                BatteryPercent = null,
                State = BatteryState.Unknown,
                Note = "罗技接收器在线（鼠标休眠中）",
            },
            new()
            {
                Id = "logi:C0A9:FF",
                VendorId = 0x046D,
                ProductId = 0xC0A9,
                Name = "Logitech PRO X3 SUPERSTRIKE",
                Kind = DeviceKind.Mouse,
                Provider = "Logitech",
                Connected = true,
                IsSleeping = false,
                Transport = "USB",
                BatteryPercent = 52,
                State = BatteryState.Charging,
            },
        };

        var result = DeviceUnifier.Unify(raw);

        var single = Assert.Single(result.Devices);
        Assert.Equal("logi:C54F:01", single.Id);
        Assert.Equal("Logitech PRO X3 SUPERSTRIKE", single.Name);
        Assert.Equal(52, single.BatteryPercent);
        Assert.Equal(BatteryState.Charging, single.State);
        Assert.False(single.IsSleeping);
        Assert.Equal("USB 充电中", single.Transport);
        Assert.Equal("logi:C0A9:FF", result.ActiveTransportRouting["logi:C54F:01"]);
    }

    [Fact]
    public void Unify_RazerAngryMiaoAndNameMatchedAtk_MergeIntoSingleCanonicalDevice()
    {
        var raw = new List<PeripheralDevice>
        {
            new()
            {
                Id = "razer:00C0",
                VendorId = 0x1532,
                ProductId = 0x00C0,
                Name = "Razer Viper V3 Pro Wired",
                Kind = DeviceKind.Mouse,
                Provider = "Razer",
                Connected = true,
                Transport = "USB",
                BatteryPercent = 85,
                State = BatteryState.Normal,
            },
            new()
            {
                Id = "razer:00C1",
                VendorId = 0x1532,
                ProductId = 0x00C1,
                Name = "Razer Viper V3 Pro",
                Kind = DeviceKind.Mouse,
                Provider = "Razer",
                Connected = true,
                IsSleeping = true,
                Transport = "2.4G",
                State = BatteryState.Unknown,
            },
            new()
            {
                Id = "am:0E8D-0703",
                VendorId = 0x0E8D,
                ProductId = 0x0703,
                Name = "Angry Miao AM Infinity .97",
                Kind = DeviceKind.Mouse,
                Provider = "AngryMiao",
                Connected = true,
                IsSleeping = true,
                Transport = "2.4G",
                Note = "鼠标无线休眠中（接收器备用电池仓 100%，晃动唤醒）",
            },
            new()
            {
                Id = "am:0E8D-0880",
                VendorId = 0x0E8D,
                ProductId = 0x0880,
                Name = "Angry Miao AM Infinity .97",
                Kind = DeviceKind.Mouse,
                Provider = "AngryMiao",
                Connected = true,
                IsSleeping = false,
                Transport = "USB",
                BatteryPercent = 60,
                State = BatteryState.Charging,
            },
            new()
            {
                Id = "atk:373B-9991",
                VendorId = 0x373B,
                ProductId = 0x9991,
                Name = "ATK A9 Mini",
                Kind = DeviceKind.Mouse,
                Provider = "ATK",
                Connected = true,
                IsSleeping = true,
                Transport = "2.4G",
            },
            new()
            {
                Id = "atk:373B-9992",
                VendorId = 0x373B,
                ProductId = 0x9992,
                Name = "ATK A9 Mini Wired",
                Kind = DeviceKind.Mouse,
                Provider = "ATK",
                Connected = true,
                IsSleeping = false,
                Transport = "USB",
                BatteryPercent = 100,
                State = BatteryState.FullyCharged,
            },
        };

        var result = DeviceUnifier.Unify(raw);

        Assert.Equal(3, result.Devices.Count);

        var razer = Assert.Single(result.Devices, d => d.Provider == "Razer");
        Assert.Equal("razer:00C1", razer.Id);
        Assert.Equal("Razer Viper V3 Pro", razer.Name);
        Assert.Equal("USB 有线", razer.Transport);
        Assert.Equal(85, razer.BatteryPercent);
        Assert.Equal("razer:00C0", result.ActiveTransportRouting["razer:00C1"]);

        var am = Assert.Single(result.Devices, d => d.Provider == "AngryMiao");
        Assert.Equal("am:0E8D-0703", am.Id);
        Assert.Equal("USB 充电中", am.Transport);
        Assert.Equal(60, am.BatteryPercent);
        Assert.Contains("备用电池仓 100%", am.Note ?? "");
        Assert.Equal("am:0E8D-0880", result.ActiveTransportRouting["am:0E8D-0703"]);

        var atk = Assert.Single(result.Devices, d => d.Provider == "ATK");
        Assert.Equal("atk:373B-9991", atk.Id);
        Assert.Equal("ATK A9 Mini", atk.Name);
        Assert.Equal("USB 已充满", atk.Transport);
        Assert.Equal(100, atk.BatteryPercent);
        Assert.Equal("atk:373B-9992", result.ActiveTransportRouting["atk:373B-9991"]);
    }

    [Fact]
    public void Unify_DistinctModelsOfSameBrand_NeverMerge()
    {
        var raw = new List<PeripheralDevice>
        {
            new()
            {
                Id = "wlmouse:36A7-A856",
                VendorId = 0x36A7,
                ProductId = 0xA856,
                Name = "WLMOUSE BEAST X V2",
                Kind = DeviceKind.Mouse,
                Provider = "WLmouse",
                Connected = true,
                Transport = "2.4G",
            },
            new()
            {
                Id = "wlmouse:36A7-A861",
                VendorId = 0x36A7,
                ProductId = 0xA861,
                Name = "WLMOUSE BEAST G Wired",
                Kind = DeviceKind.Mouse,
                Provider = "WLmouse",
                Connected = true,
                Transport = "USB",
            },
            new()
            {
                Id = "rog:1B7A:kb",
                VendorId = 0x0B05,
                ProductId = 0x1B7A,
                Name = "ROG 游侠 2 RX 98 无线",
                Kind = DeviceKind.Keyboard,
                Provider = "ROG",
                Connected = true,
                Transport = "Omni 2.4G",
            },
            new()
            {
                Id = "rog:1C7D:kb",
                VendorId = 0x0B05,
                ProductId = 0x1C7D,
                Name = "ROG 魔导士 Ace HFX 75",
                Kind = DeviceKind.Keyboard,
                Provider = "ROG",
                Connected = true,
                Transport = "USB 有线磁轴",
            },
            new()
            {
                Id = "atk:373B-1303",
                VendorId = 0x373B,
                ProductId = 0x1303,
                Name = "ATK A9 Mini",
                Kind = DeviceKind.Mouse,
                Provider = "ATK",
                Connected = true,
                Transport = "2.4G",
            },
            new()
            {
                Id = "atk:373B-1304",
                VendorId = 0x373B,
                ProductId = 0x1304,
                Name = "ATK A9 Mini + Wired",
                Kind = DeviceKind.Mouse,
                Provider = "ATK",
                Connected = true,
                Transport = "USB",
            },
            new()
            {
                Id = "atk:373B-9001",
                VendorId = 0x373B,
                ProductId = 0x9001,
                Name = "ATK 373B:9001",
                Kind = DeviceKind.Mouse,
                Provider = "ATK",
                Connected = true,
                Transport = "2.4G",
            },
            new()
            {
                Id = "atk:373B-9002",
                VendorId = 0x373B,
                ProductId = 0x9002,
                Name = "Wireless mouse",
                Kind = DeviceKind.Mouse,
                Provider = "ATK",
                Connected = true,
                Transport = "USB",
            },
        };

        var result = DeviceUnifier.Unify(raw);

        Assert.Equal(8, result.Devices.Count);
        Assert.Empty(result.ActiveTransportRouting);
    }

    private sealed class MockTunableProvider(string name, string prefix) : ITunablePeripheralProvider
    {
        public string Name => name;
        public Func<IReadOnlyList<PeripheralDevice>> EnumerateFunc { get; set; } = () => [];
        public string? LastQueriedId { get; private set; }
        public string? LastAppliedId { get; private set; }

        public bool CanHandleDevice(string deviceId)
            => deviceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        public IReadOnlyList<PeripheralDevice> Enumerate() => EnumerateFunc();

        public DeviceTuningCapabilities QueryCapabilities(string deviceId)
        {
            LastQueriedId = deviceId;
            return new DeviceTuningCapabilities { DeviceId = deviceId, IsOnline = true };
        }

        public DeviceTuningResult ApplySettings(string deviceId, DeviceTuningUpdate update)
        {
            LastAppliedId = deviceId;
            return new DeviceTuningResult(true, "ok", new DeviceTuningCapabilities { DeviceId = deviceId, IsOnline = true });
        }
    }

    [Fact]
    public async Task DeviceManager_PrunesRemovedDevices_RetainsOnProviderError_AndRoutesCapabilitiesAndApply()
    {
        var logi = new MockTunableProvider("MockLogi", "logi:")
        {
            EnumerateFunc = () =>
            [
                new PeripheralDevice
                {
                    Id = "logi:C54F:01",
                    VendorId = 0x046D,
                    ProductId = 0xC54F,
                    Name = "Logitech PRO X3 SUPERSTRIKE",
                    Kind = DeviceKind.Mouse,
                    Provider = "Logitech",
                    Connected = true,
                    IsSleeping = true,
                    Transport = "2.4G",
                },
                new PeripheralDevice
                {
                    Id = "logi:C0A9:FF",
                    VendorId = 0x046D,
                    ProductId = 0xC0A9,
                    Name = "Logitech PRO X3 SUPERSTRIKE",
                    Kind = DeviceKind.Mouse,
                    Provider = "Logitech",
                    Connected = true,
                    IsSleeping = false,
                    Transport = "USB",
                    BatteryPercent = 52,
                    State = BatteryState.Charging,
                },
            ],
        };
        var razer = new MockTunableProvider("MockRazer", "razer:")
        {
            EnumerateFunc = () =>
            [
                new PeripheralDevice
                {
                    Id = "razer:00C1",
                    VendorId = 0x1532,
                    ProductId = 0x00C1,
                    Name = "Razer Viper V3 Pro",
                    Kind = DeviceKind.Mouse,
                    Provider = "Razer",
                    Connected = true,
                    Transport = "2.4G",
                    BatteryPercent = 90,
                    State = BatteryState.Normal,
                },
            ],
        };

        using var mgr = new DeviceManager([logi, razer]);

        // 1. Initial scan unifies Logitech + keeps Razer, routes tuning to wired ID
        await mgr.RefreshAsync(bypassCoexistenceGuard: true);
        var snap1 = mgr.LastSnapshot();
        Assert.Equal(2, snap1.Count);
        var logiCard = Assert.Single(snap1, d => d.Id == "logi:C54F:01");
        Assert.Equal("USB 充电中", logiCard.Transport);
        Assert.Equal(52, logiCard.BatteryPercent);
        Assert.Equal("logi:C0A9:FF", mgr.ResolveActiveDeviceId("logi:C54F:01"));

        var caps = await mgr.QueryCapabilitiesAsync("logi:C54F:01");
        Assert.Equal("logi:C0A9:FF", logi.LastQueriedId);
        Assert.Equal("logi:C54F:01", caps.DeviceId);

        var applyRes = await mgr.ApplySettingsAsync("logi:C54F:01", new DeviceTuningUpdate { ActiveDpiStage = 1 });
        Assert.Equal("logi:C0A9:FF", logi.LastAppliedId);
        Assert.Equal("logi:C54F:01", applyRes.Capabilities?.DeviceId);

        // 2. Physically unplugging Razer -> pruned immediately when provider returns empty cleanly
        razer.EnumerateFunc = () => [];
        await mgr.RefreshAsync(bypassCoexistenceGuard: true);
        var snap2 = mgr.LastSnapshot();
        Assert.Single(snap2);
        Assert.DoesNotContain(snap2, d => d.Id == "razer:00C1");

        // 3. Transient provider exception must retain last known Logitech device
        logi.EnumerateFunc = () => throw new InvalidOperationException("transient HID error");
        await mgr.RefreshAsync(bypassCoexistenceGuard: true);
        var snap3 = mgr.LastSnapshot();
        Assert.Single(snap3);
        Assert.Equal("logi:C54F:01", snap3[0].Id);

        // 4. Unplugging USB charging cable while dongle stays -> reverts cleanly to canonical 2.4G dongle ID
        logi.EnumerateFunc = () =>
        [
            new PeripheralDevice
            {
                Id = "logi:C54F:01",
                VendorId = 0x046D,
                ProductId = 0xC54F,
                Name = "Logitech PRO X3 SUPERSTRIKE",
                Kind = DeviceKind.Mouse,
                Provider = "Logitech",
                Connected = true,
                IsSleeping = true,
                Transport = "2.4G",
            },
        ];
        await mgr.RefreshAsync(bypassCoexistenceGuard: true);
        var snap4 = mgr.LastSnapshot();
        Assert.Single(snap4);
        Assert.True(snap4[0].IsSleeping);
        Assert.Equal("2.4G", snap4[0].Transport);
        Assert.Equal("logi:C54F:01", mgr.ResolveActiveDeviceId("logi:C54F:01"));
    }
}
