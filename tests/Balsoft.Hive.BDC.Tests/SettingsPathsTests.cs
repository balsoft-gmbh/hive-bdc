using System.Text.Json;
using System.Text.RegularExpressions;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>The run mode of the messages is global to the process, so the tests that switch it run alone.</summary>
[CollectionDefinition("global run mode", DisableParallelization = true)]
public sealed class GlobalRunModeCollection;

/// <summary>Design part 19: messages name the page the user sees, Settings in the Windows app, the console page in Azure.</summary>
[Collection("global run mode")]
public sealed class SettingsPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-paths-" + Guid.NewGuid());

    private static readonly LicenseResult License = new()
    {
        State = LicenseState.Valid,
        Info = new LicenseInfo { Features = new() { "documents", "sharepoint" } },
    };

    public void Dispose()
    {
        SettingsPaths.Azure = false;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HiveBDC.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("HiveBDC.sln not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void WindowsKeepsItsWordsAndAzureNamesTheConsolePage()
    {
        Assert.False(SettingsPaths.Azure);
        Assert.Equal("Settings > Outbound", SettingsPaths.Pick("Settings > Outbound", "Outbound"));
        SettingsPaths.Azure = true;
        Assert.Equal("Outbound", SettingsPaths.Pick("Settings > Outbound", "Outbound"));
    }

    [Fact]
    public void EveryServiceTextThatSendsTheUserToSettingsHasItsAzureWording()
    {
        string root = RepoRoot();
        var phrases = new[] { "Settings >", "back into the input folder", "in Settings." };
        var pinned = new Regex(@"SettingsPaths\.Pick\(\s*""(?:[^""\\]|\\.)*""\s*,\s*""(?:[^""\\]|\\.)*""\s*\)");
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.StartsWith("app/") || rel.StartsWith("tests/") || rel.StartsWith("setup/") || rel.Contains("/obj/") || rel.Contains("/bin/")
                || rel.StartsWith("obj/") || rel.StartsWith("bin/")) continue;
            // Word input and the column file import exist only in the Windows app; an Azure instance refuses both.
            if (rel is "Documents/DocumentProcessor.Word.cs" or "Configuration/SharePointFieldImport.cs") continue;
            int no = 0;
            foreach (var raw in File.ReadLines(file))
            {
                no++;
                string line = raw.TrimStart();
                if (line.StartsWith("//")) continue;
                string rest = pinned.Replace(line, "");
                foreach (var phrase in phrases)
                    if (rest.Contains(phrase) && rest.Contains('"')) missing.Add($"{rel}:{no}: {line}");
            }
        }
        Assert.True(missing.Count == 0, "Texts without an Azure wording:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void OnAzureASwitchedOffTypeSendsTheUserToOutboundAndRetry()
    {
        SettingsPaths.Azure = true;
        string input = Path.Combine(_root, "A");
        Directory.CreateDirectory(input);
        var route = new DocumentRoute { DataAreaId = "A", Input = input, Output = Path.Combine(input, "out") };
        var options = new BDCOptions();
        options.DocumentTypes.Add(new DocumentTypeOptions { Id = "PurchaseOrder", Handler = DocumentHandlerKind.Pdf, Mail = false, Companies = { route } });
        var off = new DocumentTypeOptions { Id = "SalesQuotation", Enabled = false, Mail = false };
        off.Companies.Add(new DocumentRoute { DataAreaId = "A", Input = input });
        options.DocumentTypes.Add(off);
        options.Normalize();
        string quote = Path.Combine(input, "quote_1.json");
        File.WriteAllText(quote, JsonSerializer.Serialize(new { documentType = "SalesQuotation", dataAreaId = "A" }));
        File.WriteAllText(Path.ChangeExtension(quote, ".pdf"), "Synthetic quotation PDF");
        var ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, Path.Combine(_root, "hive.db"));

        var result = new DocumentProcessor(NullLogger.Instance, new DocumentTypeRegistry(Path.Combine(_root, "settings.json"), NullLogger.Instance), ledger)
            .ScanOnce(options, License, null, null);

        Assert.Equal(1, result.Errors);
        string text = string.Join("\n", ledger.Events("quote", 20).Select(e => e.Message));
        Assert.Contains("Document type 'SalesQuotation' is switched off in Outbound. Switch it on, then press Retry in the document history.", text);
        Assert.Single(Regex.Matches(text, "Retry in the document history"));
        Assert.DoesNotContain("Settings >", text);
        Assert.DoesNotContain("input folder", text);
    }
}