using System.Text.Json.Nodes;
using Balsoft.Hive.BDC.App.ViewModels;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>The Windows app's "Goes to" of an inbound company (design 2026-11-20, part 3 and failure cases 8 and 14).</summary>
public sealed class WindowsInboundTests
{
    private static readonly KeyValuePair<string, string>[] NoFields = Array.Empty<KeyValuePair<string, string>>();

    [Fact]
    public void ADestinationKindTheAppDoesNotKnowIsWrittenBackUnchanged()
    {
        var original = JsonNode.Parse("""{"Kind":"ftp","Connection":"erp-ftp","Enabled":true,"Settings":{"Host":"erp","Path":"/in"},"Retry":3}""")!.AsObject();

        var written = InboundTargetJson.Build("ftp", known: false, original, folder: "", saveFile: true, connection: "", NoFields);

        Assert.Equal(original.ToJsonString(), written.ToJsonString());
        Assert.NotSame(original, written);
    }

    [Fact]
    public void AFolderIsWrittenAsVersion110WroteIt()
    {
        var written = InboundTargetJson.Build("folder", known: true, null, @" C:\ERP\in ", saveFile: true, connection: "x", NoFields);

        Assert.Equal("""{"Kind":"folder","Enabled":true,"Settings":{"Folder":"C:\\ERP\\in","SaveFile":"true"}}""", written.ToJsonString());
    }

    [Fact]
    public void BlobStorageGetsItsConnectionTheFileSwitchAndItsFields()
    {
        var written = InboundTargetJson.Build("blob", known: true, null, @"C:\old", saveFile: false, connection: " erp-storage ",
            new[] { new KeyValuePair<string, string>("Container", "erp-inbound "), new KeyValuePair<string, string>("Prefix", "pending/") });

        Assert.Equal("""{"Kind":"blob","Connection":"erp-storage","Enabled":true,"Settings":{"SaveFile":"false","Container":"erp-inbound","Prefix":"pending/"}}""",
            written.ToJsonString());
    }

    [Fact]
    public void TheGoesToSegmentsBindOneWayAndWriteThroughTheirHandler()
    {
        string page = File.ReadAllText(Path.Combine(Repo(), "app", "HiveBDC.App", "Pages", "InboundPage.xaml"));
        string code = File.ReadAllText(Path.Combine(Repo(), "app", "HiveBDC.App", "Pages", "InboundPage.xaml.cs"));

        Assert.Contains("""GroupName="InboundGoesTo" """.Trim(), page);
        Assert.Contains("""IsChecked="{Binding IsSelected, Mode=OneWay}" Tag="IsSelected" Checked="OnSegmentChecked""", page);
        Assert.Contains("record.GetType().GetProperty(property)?.SetValue(record, true);", code);
        Assert.Contains("Picked = c => DestinationKind = c.Kind", File.ReadAllText(Path.Combine(Repo(), "app", "HiveBDC.App", "ViewModels", "InboundFlowViewModel.cs")));
        Assert.Contains("""Text="{Binding UnknownDestinationText}""", page);
    }

    private static string Repo()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "HiveBDC.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}