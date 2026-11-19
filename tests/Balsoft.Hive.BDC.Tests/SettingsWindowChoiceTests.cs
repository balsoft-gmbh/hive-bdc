using System.Xml.Linq;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>
/// Two-way radios in one group write "false" into the record shown before while the window switches records; on the
/// Windows test machine (2026-11-19) opening one company after another changed its source without an edit.
/// </summary>
public class SettingsWindowChoiceTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HiveBDC.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("HiveBDC.sln not found above " + AppContext.BaseDirectory);
    }

    private static string App(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "app", "HiveBDC.App", relative)).Replace("\r\n", "\n");

    private static List<XElement> Segments() =>
        XDocument.Parse(App("SettingsWindow.xaml")).Descendants()
            .Where(e => e.Name.LocalName == "RadioButton" && (string?)e.Attribute("Style") == "{StaticResource Segment}")
            .ToList();

    [Fact]
    public void EverySegmentBindsOneWayAndSetsOnlyTheRecordOnScreen()
    {
        var segments = Segments();
        Assert.Equal(6, segments.Count);
        foreach (var radio in segments)
        {
            var property = (string?)radio.Attribute("Tag");
            Assert.False(string.IsNullOrEmpty(property));
            Assert.Equal($"{{Binding {property}, Mode=OneWay}}", (string?)radio.Attribute("IsChecked"));
            Assert.Equal("OnSegmentChecked", (string?)radio.Attribute("Checked"));
        }
        Assert.Contains("record.GetType().GetProperty(property)?.SetValue(record, true);", App("SettingsWindow.xaml.cs"));
    }

    [Fact]
    public void EverySegmentNamesABoolOfItsViewModel()
    {
        var models = string.Concat(Directory.GetFiles(Path.Combine(RepoRoot(), "app", "HiveBDC.App", "ViewModels"), "*.cs")
            .Select(File.ReadAllText));
        foreach (var property in Segments().Select(r => (string)r.Attribute("Tag")!))
        {
            var field = "_" + char.ToLowerInvariant(property[0]) + property[1..];
            Assert.True(models.Contains($"public bool {property}\n") || models.Contains($"public bool {property}\r\n")
                || models.Contains($"private bool {field};"), property);
        }
    }
}