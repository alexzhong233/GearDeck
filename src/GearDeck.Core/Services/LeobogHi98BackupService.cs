using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GearDeck.Core.Providers;

namespace GearDeck.Core.Services;

/// <summary>EEPROM 自动备份快照：layers 为 layer 索引 -> 512B 原始页。</summary>
public sealed class Hi98EepromBackupSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Reason { get; set; } = "";
    public string ModelName { get; set; } = "LEOBOG Hi98C Pro";
    public Hi98ConnectionTransport Transport { get; set; } = Hi98ConnectionTransport.Disconnected;

    public Dictionary<int, byte[]> Layers { get; set; } = [];
    public byte[]? LedConfig { get; set; }        // 128B
    public byte[]? PerKeyRgb { get; set; }        // 378B
    public byte[]? PalettePage { get; set; }      // 512B
    public Hi98MatrixCalibrationMap? Calibration { get; set; }
}

/// <summary>
/// EEPROM 快照备份与矩阵校准持久化管理器。
/// 存储根目录为系统用户数据目录下的 GearDeck（仅使用运行时 API 推导，不写入任何本机绝对路径）。
/// </summary>
public sealed class LeobogHi98BackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string BaseDirectory { get; }
    public string BackupsDirectory { get; }
    public string CalibrationFilePath { get; }

    public LeobogHi98BackupService(string? baseDirectory = null)
    {
        BaseDirectory = baseDirectory ?? DefaultBaseDirectory();
        BackupsDirectory = Path.Combine(BaseDirectory, "hi98-backups");
        CalibrationFilePath = Path.Combine(BaseDirectory, "hi98-calibration.json");
        Directory.CreateDirectory(BackupsDirectory);
    }

    private static string DefaultBaseDirectory()
    {
        string parent = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(parent))
            parent = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(parent))
            parent = Path.GetTempPath();
        return Path.Combine(parent, "GearDeck");
    }

    public string SaveBackup(Hi98EepromBackupSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Directory.CreateDirectory(BackupsDirectory);

        string stamp = snapshot.CreatedAt.LocalDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string safeReason = (snapshot.Reason ?? "")
            .Replace("/", "-", StringComparison.Ordinal)
            .Replace(":", "-", StringComparison.Ordinal)
            .Replace(" ", "_", StringComparison.Ordinal);
        safeReason = safeReason[..Math.Min(40, safeReason.Length)];
        string fileName = $"{stamp}-{safeReason}-{snapshot.Id.ToString("N")[..6]}.json";
        string filePath = Path.Combine(BackupsDirectory, fileName);

        WriteAtomic(filePath, JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions));
        return filePath;
    }

    public IReadOnlyList<(string Path, Hi98EepromBackupSnapshot Snapshot)> ListBackups()
    {
        if (!Directory.Exists(BackupsDirectory)) return [];

        var items = new List<(string, Hi98EepromBackupSnapshot)>();
        foreach (string filePath in Directory.EnumerateFiles(BackupsDirectory, "*.json"))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<Hi98EepromBackupSnapshot>(
                    File.ReadAllBytes(filePath), JsonOptions);
                if (snapshot != null) items.Add((filePath, snapshot));
            }
            catch (JsonException)
            {
                // 损坏的备份文件不参与列表。
            }
            catch (IOException)
            {
                // 被其他进程占用的备份文件跳过。
            }
        }
        return items.OrderByDescending(x => x.Item2.CreatedAt).ToList();
    }

    public void ExportBackup(Hi98EepromBackupSnapshot snapshot, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        WriteAtomic(destinationPath, JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions));
    }

    public Hi98EepromBackupSnapshot ImportBackup(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return JsonSerializer.Deserialize<Hi98EepromBackupSnapshot>(File.ReadAllBytes(sourcePath), JsonOptions)
            ?? throw new JsonException("备份文件内容为空或格式无效。");
    }

    public void SaveCalibration(Hi98MatrixCalibrationMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        Directory.CreateDirectory(BaseDirectory);
        WriteAtomic(CalibrationFilePath, JsonSerializer.SerializeToUtf8Bytes(map, JsonOptions));
    }

    public Hi98MatrixCalibrationMap? LoadCalibration()
    {
        if (!File.Exists(CalibrationFilePath)) return null;
        try
        {
            var map = JsonSerializer.Deserialize<Hi98MatrixCalibrationMap>(
                File.ReadAllBytes(CalibrationFilePath), JsonOptions);
            return map?.Sanitized();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>模拟 Foundation 的 .atomic 写入：先写同目录临时文件，再原子替换。</summary>
    private static void WriteAtomic(string path, byte[] data)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory ?? ".",
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
