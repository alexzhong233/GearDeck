using System.Xml.Linq;
using GearDeck.Metrics;
using Xunit;

public class XamlSafetyTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GearDeck.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find GearDeck.slnx root.");
    }

    [Fact]
    public void AllAppXamlRangeControls_SetSafeMinimumOrder()
    {
        string appDir = Path.Combine(FindRepoRoot(), "old", "GearDeck.App");
        if (!Directory.Exists(appDir)) return;
        var xamlFiles = Directory.GetFiles(appDir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(xamlFiles);

        foreach (string file in xamlFiles)
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var el in doc.Descendants().Where(e => e.Name.LocalName is "Slider" or "ProgressBar"))
            {
                var attrs = el.Attributes().ToList();
                var minAttr = attrs.FirstOrDefault(a => a.Name.LocalName == "Minimum");
                var maxAttr = attrs.FirstOrDefault(a => a.Name.LocalName == "Maximum");
                if (minAttr == null) continue;

                double minVal = double.Parse(minAttr.Value, System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(minVal <= 1.0, $"{Path.GetFileName(file)}: {el.Name.LocalName} has Minimum={minVal} (>1.0 risks WinUI RangeBase XamlParseException). Use Minimum=\"0\" in XAML and set real bounds in C#.");
                if (maxAttr != null && minVal > 0)
                {
                    int minIdx = attrs.IndexOf(minAttr);
                    int maxIdx = attrs.IndexOf(maxAttr);
                    Assert.True(maxIdx < minIdx, $"{Path.GetFileName(file)}: Maximum must appear before Minimum on {el.Name.LocalName}.");
                }
            }
        }
    }

    [Fact]
    public void AllAppXamlCheckBoxesAndToggles_AvoidInitialTrueWithInlineHandler()
    {
        string appDir = Path.Combine(FindRepoRoot(), "old", "GearDeck.App");
        if (!Directory.Exists(appDir)) return;
        var xamlFiles = Directory.GetFiles(appDir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(xamlFiles);

        foreach (string file in xamlFiles)
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var cb in doc.Descendants().Where(e => e.Name.LocalName == "CheckBox"))
            {
                bool hasInitialTrue = string.Equals(cb.Attribute("IsChecked")?.Value, "True", StringComparison.OrdinalIgnoreCase);
                bool hasInlineHandler = cb.Attribute("Checked") != null || cb.Attribute("Unchecked") != null;
                Assert.False(
                    hasInitialTrue && hasInlineHandler,
                    $"{Path.GetFileName(file)}: CheckBox '{cb.Attribute("Name")?.Value ?? cb.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value}' sets IsChecked=\"True\" with inline Checked/Unchecked handler, which fires before subsequent controls exist during InitializeComponent.");
            }

            foreach (var ts in doc.Descendants().Where(e => e.Name.LocalName == "ToggleSwitch"))
            {
                bool hasInitialTrue = string.Equals(ts.Attribute("IsOn")?.Value, "True", StringComparison.OrdinalIgnoreCase);
                bool hasInlineHandler = ts.Attribute("Toggled") != null;
                Assert.False(
                    hasInitialTrue && hasInlineHandler,
                    $"{Path.GetFileName(file)}: ToggleSwitch '{ts.Attribute("Name")?.Value ?? ts.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value}' sets IsOn=\"True\" with inline Toggled handler.");
            }

            foreach (var nb in doc.Descendants().Where(e => e.Name.LocalName == "NumberBox"))
            {
                bool hasInitialVal = nb.Attribute("Value") != null;
                bool hasInlineHandler = nb.Attribute("ValueChanged") != null;
                Assert.False(
                    hasInitialVal && hasInlineHandler,
                    $"{Path.GetFileName(file)}: NumberBox '{nb.Attribute("Name")?.Value ?? nb.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value}' sets Value with inline ValueChanged handler.");
            }
        }
    }

    [Fact]
    public void SystemMetricsEngine_Exposes13MetricsAndFormatsCleanly()
    {
        Assert.Equal(13, SystemMetricsEngine.AllMetrics.Count);
        var snap = new SystemMetricsSnapshot
        {
            CpuPercent = 24.4,
            CpuFreqGhz = 4.75,
            CpuTempC = 61,
            RamPercent = 52.0,
            MemUsedGb = 16.6,
            MemTotalGb = 32.0,
            GpuPercent = 31.0,
            GpuTempC = 54,
            VramUsedGb = 5.2,
            VramTotalGb = 16.0,
            NetDownBytesPerSec = 2_400_000,
            NetUpBytesPerSec = 180_000,
            DiskActivePercent = 4.0,
            SystemDriveLabel = "C:",
            DiskSpaceUsedPercent = 65.0,
            DiskFreeGb = 320.0,
            Uptime = TimeSpan.FromMinutes(195),
        };

        foreach (var def in SystemMetricsEngine.AllMetrics)
        {
            string tb = SystemMetricsEngine.FormatTaskbar(def.Id, snap);
            string detail = SystemMetricsEngine.FormatDetailed(def.Id, snap);
            Assert.False(string.IsNullOrWhiteSpace(tb), $"Taskbar text empty for {def.Id}");
            Assert.False(string.IsNullOrWhiteSpace(detail), $"Detailed text empty for {def.Id}");
        }
        Assert.Equal("", SystemMetricsEngine.FormatTaskbar("NONE", snap));
    }

    [Fact]
    public void AllDesktopAxaml_EnforceAvaloniaSafetyRules()
    {
        string desktopDir = Path.Combine(FindRepoRoot(), "src", "GearDeck.Desktop");
        var axamlFiles = Directory.GetFiles(desktopDir, "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(axamlFiles);

        foreach (string file in axamlFiles)
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            string name = Path.GetFileName(file);

            foreach (var el in doc.Descendants().Where(e => e.Name.LocalName is "Slider" or "ProgressBar"))
            {
                var attrs = el.Attributes().ToList();
                var minAttr = attrs.FirstOrDefault(a => a.Name.LocalName == "Minimum");
                var maxAttr = attrs.FirstOrDefault(a => a.Name.LocalName == "Maximum");
                if (minAttr == null) continue;

                double minVal = double.Parse(minAttr.Value, System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(minVal <= 1.0, $"{name}: {el.Name.LocalName} has Minimum={minVal} (>1.0).");
                if (maxAttr != null)
                {
                    int minIdx = attrs.IndexOf(minAttr);
                    int maxIdx = attrs.IndexOf(maxAttr);
                    Assert.True(maxIdx < minIdx, $"{name}: Maximum must appear before Minimum on {el.Name.LocalName}.");
                }
            }

            foreach (var ts in doc.Descendants().Where(e => e.Name.LocalName == "ToggleSwitch"))
                Assert.Null(ts.Attribute("Header"));

            foreach (var grid in doc.Descendants().Where(e => e.Name.LocalName == "Grid"))
            {
                Assert.Null(grid.Attribute("ColumnSpacing"));
                Assert.Null(grid.Attribute("RowSpacing"));
            }

            foreach (var nb in doc.Descendants().Where(e => e.Name.LocalName == "NumberBox"))
            {
                Assert.False(nb.Attribute("Value") != null && nb.Attribute("ValueChanged") != null,
                    $"{name}: NumberBox sets Value with inline ValueChanged handler.");
            }
        }
    }
}
