using System.Text.Json.Nodes;
using Balsoft.Hive.BDC.Configuration;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>Failure case 54 (part 14b): the three-way merge behind "Save both" and the change lists of the refusal.</summary>
public sealed class ConfigMergeTests
{
    private static JsonObject Section(string folder = "{InvoiceDate:yyyy}", bool docuWare = false, int poll = 30) => JsonNode.Parse($$"""
        {
          "PollSeconds": {{poll}},
          "DocumentTypes": [
            { "Id": "SalesInvoice", "Enabled": true, "Companies": [
              { "DataAreaId": "1000", "Input": "/home/in/1000",
                "SharePoint": { "Enabled": true, "Library": "Sales Invoices", "Folder": "{{folder}}" },
                "Targets": [ { "Kind": "docuware", "Connection": "dw", "Enabled": {{(docuWare ? "true" : "false")}}, "Settings": { "FileCabinet": "Datapool" } } ] },
              { "DataAreaId": "DEMF", "Input": "/home/in/DEMF", "SharePoint": { "Enabled": false } }
            ] }
          ],
          "Connections": [ { "Id": "dw", "Kind": "docuware", "Secret": "kv:dw" } ]
        }
        """)!.AsObject();

    [Fact]
    public void TheVersionFollowsTheContentAndIgnoresWhoSaved()
    {
        var a = Section();
        var b = Section();
        b["LastSaved"] = new JsonObject { ["By"] = "anna@contoso.example", ["At"] = "2026-11-18T10:44:00Z" };
        Assert.Equal(ConfigMerge.Version(a), ConfigMerge.Version(b));
        Assert.NotEqual(ConfigMerge.Version(a), ConfigMerge.Version(Section(poll: 60)));
        Assert.Equal(ConfigMerge.Version(null), ConfigMerge.Version(null));
        Assert.NotEqual(ConfigMerge.Version(null), ConfigMerge.Version(a));
    }

    [Fact]
    public void ChangesToDifferentSettingsOfTheSameCompanyAreBothKept()
    {
        var merge = ConfigMerge.Merge3(Section(), Section(folder: "{InvoiceDate:yyyy}/{InvoiceDate:MM}"), Section(docuWare: true));
        Assert.Empty(merge.Conflicts);
        var company = merge.Result!["DocumentTypes"]![0]!["Companies"]![0]!;
        Assert.Equal("{InvoiceDate:yyyy}/{InvoiceDate:MM}", (string?)company["SharePoint"]!["Folder"]);
        Assert.True((bool)company["Targets"]![0]!["Enabled"]!);
        Assert.Equal("DEMF", (string?)merge.Result["DocumentTypes"]![0]!["Companies"]![1]!["DataAreaId"]);
    }

    [Fact]
    public void TheSameSettingChangedTwoWaysIsAConflictNamedByItsPlace()
    {
        var merge = ConfigMerge.Merge3(Section(), Section(folder: "A"), Section(folder: "B"));
        Assert.Equal(new[] { "SalesInvoice › company 1000 › SharePoint › Folder" }, merge.Conflicts);
    }

    [Fact]
    public void BothChangingToTheSameValueIsNoConflict()
    {
        var merge = ConfigMerge.Merge3(Section(), Section(poll: 60), Section(poll: 60));
        Assert.Empty(merge.Conflicts);
        Assert.Equal(60, (int)merge.Result!["PollSeconds"]!);
    }

    [Fact]
    public void ACompanyAddedByOneAndASettingChangedByTheOtherAreBothKept()
    {
        var mine = Section();
        ((JsonArray)mine["DocumentTypes"]![0]!["Companies"]!).Add(JsonNode.Parse("""{ "DataAreaId": "2000", "Input": "/home/in/2000" }"""));
        var merge = ConfigMerge.Merge3(Section(), Section(poll: 45), mine);
        Assert.Empty(merge.Conflicts);
        Assert.Equal(45, (int)merge.Result!["PollSeconds"]!);
        Assert.Equal(new[] { "1000", "DEMF", "2000" },
            merge.Result["DocumentTypes"]![0]!["Companies"]!.AsArray().Select(c => (string?)c!["DataAreaId"]));
    }

    [Fact]
    public void ACompanyRemovedByOneStaysRemovedWhenTheOtherDidNotTouchIt()
    {
        var theirs = Section();
        ((JsonArray)theirs["DocumentTypes"]![0]!["Companies"]!).RemoveAt(1);
        var merge = ConfigMerge.Merge3(Section(), theirs, Section(docuWare: true));
        Assert.Empty(merge.Conflicts);
        Assert.Equal(new[] { "1000" }, merge.Result!["DocumentTypes"]![0]!["Companies"]!.AsArray().Select(c => (string?)c!["DataAreaId"]));
    }

    [Fact]
    public void ACompanyRemovedByOneAndChangedByTheOtherIsAConflict()
    {
        var theirs = Section();
        ((JsonArray)theirs["DocumentTypes"]![0]!["Companies"]!).RemoveAt(0);
        var merge = ConfigMerge.Merge3(Section(), theirs, Section(docuWare: true));
        Assert.Equal(new[] { "SalesInvoice › company 1000" }, merge.Conflicts);
    }

    [Fact]
    public void ChangeListsNameEachSettingWithBeforeAndAfter()
    {
        var changes = ConfigMerge.Changes(Section(), Section(folder: "{InvoiceDate:yyyy}/{InvoiceDate:MM}", docuWare: true));
        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.Path == "SalesInvoice › company 1000 › SharePoint › Folder"
            && c.Before == "{InvoiceDate:yyyy}" && c.After == "{InvoiceDate:yyyy}/{InvoiceDate:MM}");
        Assert.Contains(changes, c => c.Path == "SalesInvoice › company 1000 › docuware dw Datapool › Enabled"
            && c.Before == "false" && c.After == "true");
    }

    [Fact]
    public void SecretsAreNamedButTheirValuesAreNeverListed()
    {
        var after = Section();
        after["Connections"]![0]!["Secret"] = "kv:connection-dw-2";
        var change = Assert.Single(ConfigMerge.Changes(Section(), after));
        Assert.Equal("connection dw › Secret", change.Path);
        Assert.Equal("(hidden)", change.Before);
        Assert.Equal("(hidden)", change.After);
    }

    [Fact]
    public void DefaultsBothSavesFillInAreNotListedAsAChangeOfEither()
    {
        var start = JsonNode.Parse("""{ "PollSeconds": 30 }""")!.AsObject();
        var theirs = JsonNode.Parse("""{ "PollSeconds": 45, "LogDir": "", "Mail": { "Enabled": false } }""")!.AsObject();
        var mine = JsonNode.Parse("""{ "PollSeconds": 30, "LogDir": "", "Mail": { "Enabled": false }, "Flows": [] }""")!.AsObject();
        var (their, yours) = ConfigMerge.ChangeLists(start, theirs, mine);
        Assert.Equal(new[] { "PollSeconds" }, their.Select(c => c.Path));
        Assert.Equal(new[] { "Flows" }, yours.Select(c => c.Path));
    }

    [Fact]
    public void ListsWithoutAUniqueKeyAreComparedAsAWhole()
    {
        var mine = JsonNode.Parse("""{ "Mail": { "RedirectTo": ["a@x.example", "b@x.example"] } }""")!.AsObject();
        var theirs = JsonNode.Parse("""{ "Mail": { "RedirectTo": ["a@x.example", "c@x.example"] } }""")!.AsObject();
        var start = JsonNode.Parse("""{ "Mail": { "RedirectTo": ["a@x.example"] } }""")!.AsObject();
        Assert.Equal(new[] { "Mail › RedirectTo" }, ConfigMerge.Merge3(start, theirs, mine).Conflicts);
    }
}