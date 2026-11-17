using System.Text.Json.Nodes;
using Balsoft.Hive.BDC.App.Services;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>The settings window asks before closing only when something would be lost.</summary>
public sealed class UnsavedChangesTests
{
    private sealed class Company
    {
        public string Id { get; set; } = "";
        public List<string> Targets { get; set; } = new();
    }

    private sealed class Form
    {
        public int PollSeconds { get; set; } = 10;
        public List<Company> Companies { get; set; } = new();
        public JsonArray Flows { get; set; } = new();
        public string? Secret { get; set; }
    }

    private static Form Loaded() => new()
    {
        Companies = { new Company { Id = "1000", Targets = { "sharepoint" } } },
        Flows = new JsonArray(new JsonObject { ["Id"] = "orders", ["Enabled"] = true }),
    };

    [Fact]
    public void NothingChangedAfterLoadOrAfterSaveMeansCloseWithoutAsking()
    {
        var unsaved = new UnsavedChanges();
        var form = Loaded();
        unsaved.Remember(form);
        Assert.False(unsaved.Differ(form, passwordTyped: false));

        form.PollSeconds = 30;
        Assert.True(unsaved.Differ(form, false));
        unsaved.Remember(form);
        Assert.False(unsaved.Differ(form, false));
    }

    [Fact]
    public void AChangeDeepInsideACompanyOrAFlowCounts()
    {
        var unsaved = new UnsavedChanges();
        var form = Loaded();
        unsaved.Remember(form);

        form.Companies[0].Targets.Add("docuware");
        Assert.True(unsaved.Differ(form, false));
        form.Companies[0].Targets.Remove("docuware");
        Assert.False(unsaved.Differ(form, false));

        form.Flows[0]!["Enabled"] = false;
        Assert.True(unsaved.Differ(form, false));
    }

    [Fact]
    public void ATypedPasswordCountsEvenWhenTheFormIsTheSame()
    {
        var unsaved = new UnsavedChanges();
        var form = Loaded();
        unsaved.Remember(form);

        Assert.True(unsaved.Differ(form, passwordTyped: true));
    }

    [Fact]
    public void BeforeTheSettingsLoadedNothingCanBeLost()
        => Assert.False(new UnsavedChanges().Differ(Loaded(), passwordTyped: true));
}