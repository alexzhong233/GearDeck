using GearDeck.Core.Models;

namespace GearDeck.Core.Providers;

/// <summary>
/// Logitech HID++ 2.0 `0x8100` (Onboard Profiles) 经典布局板载硬件宏协议。
/// 仅对已验证的经典 256B 扇区格式 (`memory_model_id == 0x01`, `macro_format_id == 0x01`,
/// `profile_format_id` 1..5，且剩余扇区充足) 开放写入，严禁盲写新款未知布局固件。
/// 参考实现：`libratbag` (`src/hidpp20.c` / `src/hidpp20.h` / `src/hidpp-generic.c`)。
/// </summary>
internal static class LogitechOnboardMacroProtocol
{
    public const byte FnGetProfilesDescr = 0;
    public const byte FnSetOnboardMode = 1;
    public const byte FnGetOnboardMode = 2;
    public const byte FnSetCurrentProfile = 3;
    public const byte FnGetCurrentProfile = 4;
    public const byte FnMemoryRead = 5;
    public const byte FnMemoryAddrWrite = 6;
    public const byte FnMemoryWrite = 7;
    public const byte FnMemoryWriteEnd = 8;

    private const int ProfileButtonsOffset = 32;
    private const int MaxButtonsPerProfile = 16;

    internal readonly record struct OnboardProfilesDescriptor(
        byte MemoryModelId,
        byte ProfileFormatId,
        byte MacroFormatId,
        byte ProfileCount,
        byte ProfileCountOob,
        byte ButtonCount,
        byte SectorCount,
        ushort SectorSize);

    /// <summary>计算 Logitech 0x8100 扇区校验 CRC-CCITT (seed=0xFFFF, poly=0x1021)。</summary>
    public static ushort CrcCcitt(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < data.Length; i++)
        {
            ushort temp = (ushort)((crc >> 8) ^ data[i]);
            crc <<= 8;
            ushort quick = (ushort)(temp ^ (temp >> 4));
            crc ^= quick;
            quick <<= 5;
            crc ^= quick;
            quick <<= 7;
            crc ^= quick;
        }
        return crc;
    }

    public static OnboardProfilesDescriptor? ParseDescriptor(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 9)
            return null;
        return new OnboardProfilesDescriptor(
            MemoryModelId: reply[0],
            ProfileFormatId: reply[1],
            MacroFormatId: reply[2],
            ProfileCount: reply[3],
            ProfileCountOob: reply[4],
            ButtonCount: reply[5],
            SectorCount: reply[6],
            SectorSize: (ushort)((reply[7] << 8) | reply[8]));
    }

    public static bool IsSupportedClassicMacroLayout(OnboardProfilesDescriptor d)
        => d.MemoryModelId == 0x01
        && d.MacroFormatId == 0x01
        && d.ProfileFormatId is >= 1 and <= 5
        && d.SectorSize == 256
        && d.ProfileCount is >= 1 and <= 5
        && d.ButtonCount >= 3
        && d.SectorCount >= d.ProfileCount + 2;

    /// <summary>按 HID++ 2.0 `union hidpp20_macro_data`（3B 指令 + 0xFF 结束符）编码 256B 宏扇区。</summary>
    public static byte[] EncodeMacroSector(OnboardMacroDefinition macro, int sectorSize = 256)
    {
        var sector = new byte[sectorSize];
        Array.Fill(sector, (byte)0xFF);
        int pos = 0;
        int limit = sectorSize - 2; // 至少预留循环控制字节与 0xFF 结束符

        foreach (var step in macro.Steps)
        {
            if (pos + 3 > limit)
                break;

            if (step.KeyCode is >= 0x04 and < 0xF0)
            {
                sector[pos++] = step.IsPress ? (byte)0x43 : (byte)0x44; // HIDPP20_MACRO_KEY_PRESS / RELEASE
                sector[pos++] = 0x00;
                sector[pos++] = step.KeyCode;
            }
            else if (step.KeyCode is >= HidKeyCatalog.MouseLeftCode and <= HidKeyCatalog.MouseXButton2Code)
            {
                byte mask = (byte)(1 << (step.KeyCode - HidKeyCatalog.MouseLeftCode));
                sector[pos++] = step.IsPress ? (byte)0x41 : (byte)0x42; // HIDPP20_MACRO_BUTTON_DOWN / UP
                sector[pos++] = 0x00;
                sector[pos++] = mask;
            }

            if (step.DelayMs > 0 && pos + 3 <= limit)
            {
                ushort ms = (ushort)Math.Clamp(step.DelayMs, 1, 65535);
                sector[pos++] = 0x40; // HIDPP20_MACRO_DELAY (big-endian uint16)
                sector[pos++] = (byte)(ms >> 8);
                sector[pos++] = (byte)(ms & 0xFF);
            }
        }

        if (macro.LoopMode == MacroLoopMode.WhileHeld && pos < sectorSize - 1)
            sector[pos++] = 0x02; // HIDPP20_MACRO_REPEAT_WHILE_PRESSED
        else if (macro.LoopMode == MacroLoopMode.ToggleUntilKey && pos < sectorSize - 1)
            sector[pos++] = 0x03; // HIDPP20_MACRO_REPEAT_UNTIL_CANCELED

        if (pos < sectorSize)
            sector[pos] = 0xFF; // HIDPP20_MACRO_END
        return sector;
    }

    /// <summary>校验配置扇区现有 CRC-CCITT 后，将指定按键槽位改写为宏指针并重算 CRC。</summary>
    public static bool TryPatchProfileButtonWithMacro(
        byte[] profileSector,
        int buttonIndex,
        byte macroPage,
        byte macroOffset = 0)
    {
        if (profileSector.Length != 256 || (uint)buttonIndex >= MaxButtonsPerProfile)
            return false;

        ushort expectedCrc = (ushort)((profileSector[254] << 8) | profileSector[255]);
        ushort actualCrc = CrcCcitt(profileSector.AsSpan(0, 254));
        if (expectedCrc != actualCrc)
            return false;

        int at = ProfileButtonsOffset + buttonIndex * 4;
        profileSector[at] = 0x00; // HIDPP20_BUTTON_MACRO
        profileSector[at + 1] = macroPage;
        profileSector[at + 2] = 0x00;
        profileSector[at + 3] = macroOffset;

        ushort nextCrc = CrcCcitt(profileSector.AsSpan(0, 254));
        profileSector[254] = (byte)(nextCrc >> 8);
        profileSector[255] = (byte)(nextCrc & 0xFF);
        return true;
    }

    public static byte[]? ReadSector(
        Func<byte, byte[]?, (byte[]? Reply, byte Error)> call,
        ushort sector,
        ushort sectorSize)
    {
        var data = new byte[sectorSize];
        for (int offset = 0; offset < sectorSize; offset += 16)
        {
            int aligned = sectorSize - offset < 16 ? Math.Max(0, sectorSize - 16) : offset;
            var (reply, err) = call(FnMemoryRead,
            [
                (byte)(sector >> 8), (byte)(sector & 0xFF),
                (byte)(aligned >> 8), (byte)(aligned & 0xFF),
            ]);
            if (err != 0 || reply == null || reply.Length < 16)
                return null;
            Array.Copy(reply, 0, data, aligned, 16);
        }
        return data;
    }

    public static bool WriteSector(
        Func<byte, byte[]?, (byte[]? Reply, byte Error)> call,
        ushort sector,
        byte[] data,
        bool writeCrc)
    {
        int size = data.Length;
        if (writeCrc && size >= 2)
        {
            ushort crc = CrcCcitt(data.AsSpan(0, size - 2));
            data[size - 2] = (byte)(crc >> 8);
            data[size - 1] = (byte)(crc & 0xFF);
        }

        var (_, startErr) = call(FnMemoryAddrWrite,
        [
            (byte)(sector >> 8), (byte)(sector & 0xFF),
            0x00, 0x00,
            (byte)(size >> 8), (byte)(size & 0xFF),
        ]);
        if (startErr != 0)
            return false;

        var chunk = new byte[16];
        for (int off = 0; off < size; off += 16)
        {
            Array.Clear(chunk);
            Array.Copy(data, off, chunk, 0, Math.Min(16, size - off));
            var (_, wrErr) = call(FnMemoryWrite, chunk);
            if (wrErr != 0)
                return false;
        }

        var (_, endErr) = call(FnMemoryWriteEnd, null);
        return endErr == 0;
    }

    public static (bool Success, string Message) UploadMacro(
        Func<byte, byte[]?, (byte[]? Reply, byte Error)> call,
        int macroSlot,
        int targetButtonSlot,
        OnboardMacroDefinition macro)
    {
        var (descReply, descErr) = call(FnGetProfilesDescr, null);
        if (descErr != 0 || descReply == null || ParseDescriptor(descReply) is not { } desc)
            return (false, "读取罗技 0x8100 板载布局描述符失败。");

        if (!IsSupportedClassicMacroLayout(desc))
        {
            return (false,
                $"当前固件布局 (model=0x{desc.MemoryModelId:X2}, prof=0x{desc.ProfileFormatId:X2}, macro=0x{desc.MacroFormatId:X2}, sector={desc.SectorSize}B) 非经典已验证布局，已拒绝直写以防损坏配置，请使用官方 G HUB 写入或改用软件宏。");
        }

        var (modeReply, _) = call(FnGetOnboardMode, null);
        bool wasHostMode = modeReply is { Length: >= 1 } && modeReply[0] != 1;
        if (wasHostMode)
        {
            call(FnSetOnboardMode, [0x01]);
            Thread.Sleep(35);
        }

        ushort activeProfileSector = 1;
        var (curReply, _) = call(FnGetCurrentProfile, null);
        if (curReply is { Length: >= 2 })
        {
            int cur = curReply[1];
            if (cur >= 1 && cur <= desc.ProfileCount)
                activeProfileSector = (ushort)cur;
        }

        var profileData = ReadSector(call, activeProfileSector, desc.SectorSize);
        if (profileData == null)
            return (false, $"读取板载配置扇区 {activeProfileSector} 失败（建议先用有线连接再写入板载宏）。");

        int minMacroPage = desc.ProfileCount + 1;
        int maxMacroPage = desc.SectorCount - 1;
        byte targetMacroPage = (byte)Math.Clamp(minMacroPage + macroSlot, minMacroPage, maxMacroPage);

        byte[] macroSector = EncodeMacroSector(macro, desc.SectorSize);
        if (!WriteSector(call, targetMacroPage, macroSector, writeCrc: false))
            return (false, $"写入罗技宏扇区 {targetMacroPage} 失败。");

        if (!TryPatchProfileButtonWithMacro(profileData, targetButtonSlot, targetMacroPage, 0))
            return (false, "板载配置扇区 CRC-CCITT 校验未通过，已中止回写以保护原有板载配置（可先在 G HUB 重建一次板载配置后再试）。");

        if (!WriteSector(call, activeProfileSector, profileData, writeCrc: true))
            return (false, $"更新板载配置扇区 {activeProfileSector} 按键指针失败。");

        call(FnSetCurrentProfile, [(byte)(activeProfileSector >> 8), (byte)(activeProfileSector & 0xFF)]);
        return (true, $"已将宏「{macro.Name}」烧录至罗技板载扇区 {targetMacroPage} 并绑定到槽位 {targetButtonSlot + 1}。");
    }
}
