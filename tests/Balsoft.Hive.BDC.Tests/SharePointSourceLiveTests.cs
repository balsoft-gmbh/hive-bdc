using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>
/// An inbound flow against a real SharePoint library: a test file marked ready by its Title becomes a CSV plus the file,
/// and its Title is set to the done value. Runs only with a SharePoint app that has write on the site.
/// </summary>
public sealed class SharePointSourceLiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-sp-live-" + Guid.NewGuid());
    private static readonly HttpClient Http = new();

    public SharePointSourceLiveTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    [SharePointLiveFact]
    public async Task AFileMarkedReadyInALibraryBecomesTheCsvAndIsMarkedDone()
    {
        var auth = new SharePointAuthOptions { TenantId = Env("HIVEBDC_SP_TENANT_ID"), ClientId = Env("HIVEBDC_SP_CLIENT_ID"), ClientSecret = Env("HIVEBDC_SP_CLIENT_SECRET") };
        string siteUrl = Env("HIVEBDC_SP_SITE_URL"), library = Env("HIVEBDC_SP_LIBRARY");
        string run = Guid.NewGuid().ToString("N")[..8];
        string ready = "HiveBDC-ready-" + run, done = "HiveBDC-done-" + run;
        string token = await TokenAsync(auth);
        var site = new Uri(siteUrl);
        string siteId = (await GetAsync(token, $"https://graph.microsoft.com/v1.0/sites/{site.Host}:{site.AbsolutePath.TrimEnd('/')}?$select=id"))["id"]!.GetValue<string>();
        string listId = (await GetAsync(token, $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists?$select=id,displayName&$top=200"))["value"]!.AsArray()
            .First(l => (string?)l!["displayName"] == library)!["id"]!.GetValue<string>();
        string driveId = (await GetAsync(token, $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/drive?$select=id"))["id"]!.GetValue<string>();
        byte[] pdf = Encoding.ASCII.GetBytes("%PDF-1.4 Hive BDC inbound test " + run);
        string fileName = $"inbound-test-{run}.pdf";
        var uploaded = await SendAsync(token, HttpMethod.Put,
            $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/Inbound test/{fileName}:/content", new ByteArrayContent(pdf));
        string driveItemId = uploaded["id"]!.GetValue<string>();
        try
        {
            string itemId = (await GetAsync(token, $"https://graph.microsoft.com/v1.0/drives/{driveId}/items/{driveItemId}/listItem?$select=id"))["id"]!.GetValue<string>();
            await SendAsync(token, HttpMethod.Patch, $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/items/{itemId}/fields",
                new StringContent(new JsonObject { ["Title"] = ready }.ToJsonString(), Encoding.UTF8, "application/json"));

            var options = new BDCOptions { SharePointAuth = auth };
            string folder = Path.Combine(_root, "erp-in");
            options.Flows.Add(new FlowOptions
            {
                Id = "live-" + run, Name = "Live test", DocumentType = "Vendor invoice", DataAreaId = "USMF",
                Source = new SourceOptions
                {
                    Kind = "sharepoint",
                    Settings = { ["SiteUrl"] = siteUrl, ["Library"] = library, ["ReadyColumn"] = "Title", ["ReadyValue"] = ready, ["DoneValue"] = done },
                },
                Produce = new ProduceOptions { Columns = { new() { Name = "Company", Value = "{DataAreaId}" }, new() { Name = "File", Value = "{FileName}" }, new() { Name = "Id", Value = "{ID}" } } },
                Targets = { new TargetOptions { Kind = "folder", Settings = { ["Folder"] = folder, ["SaveFile"] = "true" } } },
            });
            options.Normalize();
            var ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, Path.Combine(_root, "hive.db"));
            var license = new LicenseResult { State = LicenseState.Valid, Info = new LicenseInfo { Features = new() { "documents", "sharepoint" } } };

            var result = new ImportProcessor(NullLogger.Instance, ledger).RunOnce(options, license);

            Assert.Equal(1, result.Processed);
            string csv = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(folder, itemId + ".csv"))[3..]);
            Assert.Equal($"Company;File;Id\r\nUSMF;{fileName};{itemId}\r\n", csv);
            Assert.Equal(pdf, File.ReadAllBytes(Path.Combine(folder, $"SP{itemId}.pdf")));
            var fields = await GetAsync(token, $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/items/{itemId}/fields?$select=Title");
            Assert.Equal(done, fields["Title"]!.GetValue<string>());
            Assert.Equal(0, new ImportProcessor(NullLogger.Instance, ledger).RunOnce(options, license).Processed);
        }
        finally
        {
            using var delete = new HttpRequestMessage(HttpMethod.Delete, $"https://graph.microsoft.com/v1.0/drives/{driveId}/items/{driveItemId}");
            delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var _ = await Http.SendAsync(delete);
        }
    }

    private static async Task<string> TokenAsync(SharePointAuthOptions auth)
    {
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = auth.ClientId, ["client_secret"] = auth.ClientSecret,
            ["scope"] = "https://graph.microsoft.com/.default", ["grant_type"] = "client_credentials",
        });
        using var response = await Http.PostAsync($"https://login.microsoftonline.com/{auth.TenantId}/oauth2/v2.0/token", body);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    private static Task<JsonNode> GetAsync(string token, string url) => SendAsync(token, HttpMethod.Get, url, null);

    private static async Task<JsonNode> SendAsync(string token, HttpMethod method, string url, HttpContent? content)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{method} {url.Split('?')[0]}: {(int)response.StatusCode} {text}");
        return JsonNode.Parse(text.Length == 0 ? "{}" : text)!;
    }
}

public sealed class SharePointLiveFactAttribute : FactAttribute
{
    public SharePointLiveFactAttribute()
    {
        if (new[] { "HIVEBDC_SP_TENANT_ID", "HIVEBDC_SP_CLIENT_ID", "HIVEBDC_SP_CLIENT_SECRET", "HIVEBDC_SP_SITE_URL", "HIVEBDC_SP_LIBRARY" }
            .Any(n => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(n))))
            Skip = "Set HIVEBDC_SP_TENANT_ID, _CLIENT_ID, _CLIENT_SECRET, _SITE_URL and _LIBRARY (an app with write on that site) to run this integration test.";
    }
}