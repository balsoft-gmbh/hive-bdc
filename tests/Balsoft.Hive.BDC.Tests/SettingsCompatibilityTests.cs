using System.Text.Json.Nodes;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>Settings files as earlier versions wrote them must load to the same routing and be written back unchanged.</summary>
public sealed class SettingsCompatibilityTests
{
    // A company entry as 1.3.4 wrote it: no InputFormat, no Targets.
    private const string Version134 = """
        {
          "HiveBDC": {
            "PollSeconds": 15,
            "DocumentTypes": [
              {
                "Id": "SalesInvoice", "DisplayName": "Sales invoice", "Party": "Customer", "Handler": "EInvoice",
                "Enabled": true, "Mail": true, "Discovered": false,
                "Companies": [
                  {
                    "DataAreaId": "001", "Input": "C:\\Export\\Invoices", "Output": "",
                    "SharePoint": { "Enabled": true, "SiteUrl": "https://tenant.example/sites/Finance", "Library": "Invoices", "Folder": "Out", "Fields": { "Title": "{DocumentNumber}" } }
                  },
                  {
                    "DataAreaId": "002", "Input": "C:\\Export\\Invoices2", "Output": "D:\\Out",
                    "SharePoint": { "Enabled": false, "SiteUrl": "", "Library": "", "Folder": "", "Fields": {} }
                  }
                ]
              }
            ]
          }
        }
        """;

    // 1.4.0 added InputFormat on the entry.
    private const string Version140 = """
        {
          "HiveBDC": {
            "DocumentTypes": [
              {
                "Id": "SalesInvoice", "DisplayName": "Sales invoice", "Party": "Customer", "Handler": "EInvoice",
                "Enabled": true, "Mail": false, "Discovered": false,
                "Companies": [
                  {
                    "DataAreaId": "001", "Input": "C:\\Export\\Word", "InputFormat": "word", "Output": "",
                    "SharePoint": { "Enabled": true, "SiteUrl": "https://tenant.example/sites/Finance", "Library": "Invoices", "Folder": "", "Fields": { "Title": "{DocumentNumber}" } }
                  }
                ]
              }
            ]
          }
        }
        """;

    private const string WithTargets = """
        {
          "HiveBDC": {
            "DocumentTypes": [
              {
                "Id": "SalesInvoice", "Party": "Customer", "Handler": "EInvoice", "Enabled": true, "Mail": true,
                "Companies": [
                  {
                    "DataAreaId": "001", "Input": "C:\\Export\\Invoices", "Output": "",
                    "SharePoint": { "Enabled": false, "SiteUrl": "", "Library": "", "Folder": "", "Fields": {} },
                    "Targets": [
                      { "Kind": "Archive", "Connection": "main", "Enabled": true, "Settings": { "Cabinet": "c-1" }, "Fields": { "NUMBER": "{DocumentNumber}" } },
                      { "Kind": "sharepoint", "Enabled": false, "Settings": { "SiteUrl": "https://tenant.example/sites/Two", "Library": "Docs" } }
                    ]
                  }
                ]
              }
            ]
          }
        }
        """;

    private static BDCOptions Load(string json)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var config = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var options = new BDCOptions();
        config.GetSection(SettingsFile.Section).Bind(options);
        options.Normalize();
        return options;
    }

    private static JsonObject Entry(string json, int index = 0)
        => (JsonObject)JsonNode.Parse(json)!["HiveBDC"]!["DocumentTypes"]![0]!["Companies"]![index]!;

    [Fact]
    public void Settings134DeliverToTheSameSharePointTargetAndNothingElse()
    {
        var options = Load(Version134);
        var type = options.DocumentTypes.Single(t => t.Id == "SalesInvoice");

        var first = type.Companies[0].EffectiveTargets().ToList();
        Assert.Single(first);
        Assert.Equal("sharepoint", first[0].Kind);
        Assert.Equal(new SharePointTarget("https://tenant.example/sites/Finance", "Invoices", "Out"),
            first[0].ToSharePoint().ResolveTarget(out _));
        Assert.Equal("{DocumentNumber}", first[0].Fields["Title"]);

        Assert.Empty(type.Companies[1].EffectiveTargets());
        Assert.Empty(type.Companies[0].Targets);
        Assert.True(type.Mail);
        Assert.False(type.Companies[0].IsWord);
        Assert.Equal(2, options.ScanFolders().Count);
        Assert.Equal(1, options.SharePointEntryCount());
    }

    [Fact]
    public void Settings140KeepWordInputAndTheSharePointTarget()
    {
        var route = Load(Version140).DocumentTypes.Single(t => t.Id == "SalesInvoice").Companies.Single();

        Assert.True(route.IsWord);
        Assert.Equal("sharepoint", route.EffectiveTargets().Single().Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AnEntryWithoutFurtherTargetsIsWrittenWithTheKeysEarlierVersionsRead(int index)
    {
        var route = Load(Version134).DocumentTypes.Single(t => t.Id == "SalesInvoice").Companies[index];

        var written = SettingsFile.RouteNode(route);

        Assert.Equal(new[] { "DataAreaId", "Input", "InputFormat", "Output", "SharePoint" }, written.Select(p => p.Key));
        Assert.True(JsonNode.DeepEquals(Entry(Version134, index)["SharePoint"], written["SharePoint"]));
        Assert.Equal(Entry(Version134, index)["Input"]!.GetValue<string>(), written["Input"]!.GetValue<string>());
    }

    [Fact]
    public void FurtherTargetsLoadWithCleanKindsAndAreWrittenBack()
    {
        var route = Load(WithTargets).DocumentTypes.Single(t => t.Id == "SalesInvoice").Companies.Single();

        Assert.Equal(new[] { "archive", "sharepoint" }, route.Targets.Select(t => t.Kind));
        Assert.Equal("main", route.Targets[0].Connection);
        Assert.Equal("c-1", route.Targets[0].Setting("cabinet"));
        Assert.Equal("archive", route.EffectiveTargets().Single().Kind);

        var written = (JsonArray)SettingsFile.RouteNode(route)["Targets"]!;
        Assert.Equal(2, written.Count);
        Assert.Equal("c-1", written[0]!["Settings"]!["Cabinet"]!.GetValue<string>());
        Assert.Equal("{DocumentNumber}", written[0]!["Fields"]!["NUMBER"]!.GetValue<string>());
        Assert.False(written[1]!["Enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void ASaveFromADashboardThatSendsNoTargetsKeepsTheStoredOnes()
    {
        var section = (JsonObject)JsonNode.Parse(WithTargets)!["HiveBDC"]!;

        var kept = SettingsFile.StoredTargets(section, "salesinvoice", "001", "C:\\Export\\Invoices\\");
        Assert.Equal(new[] { "archive", "sharepoint" }, kept.Select(t => t.Kind));
        Assert.Equal("{DocumentNumber}", kept[0].Fields["NUMBER"]);
        Assert.False(kept[1].Enabled);

        Assert.Empty(SettingsFile.StoredTargets(section, "SalesInvoice", "002", "C:\\Export\\Invoices"));
        Assert.Empty(SettingsFile.StoredTargets(section, "SalesInvoice", "001", "C:\\Elsewhere"));
        Assert.Empty(SettingsFile.StoredTargets((JsonObject)JsonNode.Parse(Version134)!["HiveBDC"]!, "SalesInvoice", "001", "C:\\Export\\Invoices"));
        Assert.Empty(SettingsFile.StoredTargets(null, "SalesInvoice", "001", "C:\\Export\\Invoices"));
    }

    [Fact]
    public void AChangedTargetChangesTheWorkerSignature()
    {
        var a = Load(WithTargets);
        var b = Load(WithTargets.Replace("c-1", "c-2"));
        var c = Load(WithTargets.Replace("{DocumentNumber}", "{CustomerNumber}"));

        Assert.NotEqual(Worker.SharePointSignature(a), Worker.SharePointSignature(b));
        Assert.NotEqual(Worker.SharePointSignature(a), Worker.SharePointSignature(c));
        Assert.Equal(Worker.SharePointSignature(a), Worker.SharePointSignature(Load(WithTargets)));
    }
}