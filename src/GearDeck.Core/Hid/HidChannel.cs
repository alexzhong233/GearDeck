using HidSharp;

namespace GearDeck.Core.Hid;

/// <summary>
/// HidSharp 之上的薄封装：短超时非阻塞读、指定 Report ID 的 Feature 交换、按接口长度自适应写入。
/// 每次探测用完即弃，避免与官方驱动长期争用通道。
/// </summary>
public sealed class HidChannel : IDisposable
{
    private readonly HidStream _stream;
    private readonly int _readBufferLength;

    public HidDevice Device { get; }
    public int OutputLength { get; }
    public int FeatureLength { get; }

    private HidChannel(HidDevice device, HidStream stream, int outputLength, int featureLength, int inputLength)
    {
        Device = device;
        _stream = stream;
        OutputLength = outputLength;
        FeatureLength = featureLength;
        _readBufferLength = Math.Max(64, Math.Max(inputLength, outputLength));
        _stream.WriteTimeout = 500;
        _stream.ReadTimeout = 100;
    }

    public static HidChannel? TryOpen(HidDevice device)
    {
        try
        {
            if (!device.TryOpen(out HidStream? stream) || stream == null)
                return null;
            return new HidChannel(
                device,
                stream,
                HidSafe.MaxOutputLen(device),
                HidSafe.MaxFeatureLen(device),
                HidSafe.MaxInputLen(device));
        }
        catch
        {
            return null;
        }
    }

    public bool WriteFeature(byte[] packet)
    {
        try
        {
            _stream.SetFeature(PadTo(packet, FeatureLength));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取指定 Report ID 的 feature report；缓冲区按接口 feature 长度分配。</summary>
    public byte[]? ReadFeature(byte reportId = 0)
    {
        try
        {
            var buffer = new byte[Math.Max(FeatureLength, 1)];
            buffer[0] = reportId;
            _stream.GetFeature(buffer);
            return buffer;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>写 output report：先按原始长度，失败再按接口输出长度补齐/截断。</summary>
    public bool WriteOutput(byte[] packet)
    {
        try
        {
            _stream.Write(packet);
            return true;
        }
        catch
        {
            try
            {
                _stream.Write(PadTo(packet, OutputLength));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>短超时读 input report；无数据返回 null（不阻塞调用方）。</summary>
    public byte[]? Read(int timeoutMs)
    {
        try
        {
            _stream.ReadTimeout = Math.Max(1, timeoutMs);
            var buffer = new byte[_readBufferLength];
            int n = _stream.Read(buffer, 0, buffer.Length);
            if (n <= 0)
                return null;
            var result = new byte[n];
            Array.Copy(buffer, result, n);
            return result;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] PadTo(byte[] packet, int length)
    {
        if (length <= 0 || packet.Length == length)
            return packet;
        var b = new byte[length];
        Array.Copy(packet, b, Math.Min(packet.Length, length));
        return b;
    }

    public void Dispose() => _stream.Dispose();
}
