using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Balsoft.Hive.BDC.Hosting;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>
/// Failure cases 39 to 43, 45 and 49 of the product design (part 13): what the Azure deployment package
/// (deploy/azure/mainTemplate.json and createUiDefinition.json) must hold for the service to start and stay safe.
/// </summary>
public sealed class DeploymentTemplateTests
{
    private const string AzurePrefix = "HiveBDC__Azure__";
    private const string SignInSecretSetting = "OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID";

    private static readonly string Root = RepoRoot();
    private static readonly JsonElement Template = Load("mainTemplate.json");
    private static readonly JsonElement Wizard = Load("createUiDefinition.json");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HiveBDC.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("HiveBDC.sln not found above " + AppContext.BaseDirectory);
    }

    private static JsonElement Load(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "deploy", "azure", name)));
        return doc.RootElement.Clone();
    }

    private static IEnumerable<JsonElement> Resources(string type)
        => Template.GetProperty("resources").EnumerateArray().Where(r => r.GetProperty("type").GetString() == type);

    private static JsonElement Named(string type, string name)
        => Resources(type).Single(r => r.GetProperty("name").GetString()!.Contains($"'{name}'", StringComparison.Ordinal));

    private static JsonElement Site => Resources("Microsoft.Web/sites").Single();

    private static JsonElement SiteConfig => Site.GetProperty("properties").GetProperty("siteConfig");

    /// <summary>An expression that only names a variable is replaced by that variable's text.</summary>
    private static string Resolve(string value)
    {
        var m = Regex.Match(value, @"^\[variables\('(?<name>[^']+)'\)\]$");
        return m.Success ? Template.GetProperty("variables").GetProperty(m.Groups["name"].Value).GetString()! : value;
    }

    private static Dictionary<string, string> AppSettings()
        => SiteConfig.GetProperty("appSettings").EnumerateArray().ToDictionary(
            s => Resolve(s.GetProperty("name").GetString()!), s => Resolve(s.GetProperty("value").GetString()!), StringComparer.Ordinal);

    [Fact]
    public void EveryHiveBdcSettingTheTemplateSetsIsOneTheServiceReads()
    {
        var settings = AppSettings();
        Assert.Equal(RunModes.Azure, settings["HiveBDC__RunMode"]);

        var known = typeof(AzureRunOptions).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var azure = settings.Keys.Where(k => k.StartsWith(AzurePrefix, StringComparison.Ordinal)).Select(k => k[AzurePrefix.Length..]).ToList();
        Assert.All(azure, name => Assert.Contains(name, known));
        Assert.Contains(nameof(AzureRunOptions.TableEndpoint), azure);
        Assert.Contains(nameof(AzureRunOptions.SettingsBlobUrl), azure);
        Assert.Contains(nameof(AzureRunOptions.KeyVaultUrl), azure);

        var others = settings.Keys.Where(k => k.StartsWith("HiveBDC__", StringComparison.Ordinal) && !k.StartsWith(AzurePrefix, StringComparison.Ordinal));
        Assert.All(others, key => Assert.Contains(key["HiveBDC__".Length..], new[] { "RunMode", "LogDir" }));
    }

    [Fact]
    public void ThePackageIsCopiedIntoTheAppAndNothingPointsAtTheDeploymentLinkAfterwards()
    {
        var settings = AppSettings();
        Assert.Equal("1", settings["WEBSITE_RUN_FROM_PACKAGE"]);
        Assert.DoesNotContain(settings.Values, v => v.Contains("_artifactsLocation", StringComparison.Ordinal));

        var deploy = Named("Microsoft.Web/sites/extensions", "onedeploy").GetProperty("properties");
        Assert.Equal("zip", deploy.GetProperty("type").GetString());
        string packageUri = Resolve(deploy.GetProperty("packageUri").GetString()!);
        Assert.StartsWith("[uri(parameters('_artifactsLocation')", packageUri);
        Assert.Contains("app/hivebdc.zip", packageUri);
        Assert.Contains("parameters('_artifactsLocationSasToken')", packageUri);
    }

    [Fact]
    public void SignInGuardsEverythingButInboundAndTheHealthCheck()
    {
        var auth = Named("Microsoft.Web/sites/config", "authsettingsV2").GetProperty("properties");
        var global = auth.GetProperty("globalValidation");
        Assert.True(global.GetProperty("requireAuthentication").GetBoolean());
        Assert.Equal("RedirectToLoginPage", global.GetProperty("unauthenticatedClientAction").GetString());
        var excluded = global.GetProperty("excludedPaths").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "/healthz", "/inbound", "/inbound/*" }, excluded);
        Assert.Equal("/healthz", SiteConfig.GetProperty("healthCheckPath").GetString());

        var registration = auth.GetProperty("identityProviders").GetProperty("azureActiveDirectory").GetProperty("registration");
        Assert.Equal("[parameters('signInClientId')]", registration.GetProperty("clientId").GetString());
        Assert.Equal(SignInSecretSetting, Resolve(registration.GetProperty("clientSecretSettingName").GetString()!));
        string issuer = registration.GetProperty("openIdIssuer").GetString()!;
        Assert.Contains("subscription().tenantId", issuer);
        Assert.Contains("/v2.0", issuer);

        string identityClient = AppSettings()[SignInSecretSetting];
        Assert.Contains("Microsoft.ManagedIdentity/userAssignedIdentities", identityClient);
        Assert.EndsWith(".clientId]", identityClient);
    }

    [Fact]
    public void DataRolesGoToTheAppsOwnIdentityAndTheSignInIdentityHasNone()
    {
        var assignments = Resources("Microsoft.Authorization/roleAssignments").Select(a => a.GetProperty("properties")).ToList();
        Assert.Equal(3, assignments.Count);
        foreach (var a in assignments)
        {
            string principal = a.GetProperty("principalId").GetString()!;
            Assert.Contains("resourceId('Microsoft.Web/sites'", principal);
            Assert.EndsWith(".identity.principalId]", principal);
            Assert.DoesNotContain("userAssignedIdentities", principal);
            Assert.Equal("ServicePrincipal", a.GetProperty("principalType").GetString());
        }

        var roles = Template.GetProperty("variables").GetProperty("roles");
        var granted = assignments.Select(a => Regex.Match(a.GetProperty("roleDefinitionId").GetString()!, @"variables\('roles'\)\.(\w+)").Groups[1].Value)
            .Select(key => roles.GetProperty(key).GetString()).ToHashSet();
        Assert.Equal(new HashSet<string?>
        {
            "0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3", // Storage Table Data Contributor
            "ba92f5b4-2d11-453d-a403-e96b0029c9fe", // Storage Blob Data Contributor
            "b86a8fe4-44ce-4948-aee5-eccb2c155cd7", // Key Vault Secrets Officer: the console writes secrets
        }, granted);

        Assert.Equal("SystemAssigned, UserAssigned", Site.GetProperty("identity").GetProperty("type").GetString());
        // AZURE_CLIENT_ID would make DefaultAzureCredential use the sign-in identity, which has no data roles.
        Assert.DoesNotContain("AZURE_CLIENT_ID", AppSettings().Keys);
    }

    [Fact]
    public void StorageVaultAndAppAcceptOnlySignedInCallersOverTls12()
    {
        var storage = Resources("Microsoft.Storage/storageAccounts").Single().GetProperty("properties");
        Assert.False(storage.GetProperty("allowSharedKeyAccess").GetBoolean());
        Assert.False(storage.GetProperty("allowBlobPublicAccess").GetBoolean());
        Assert.True(storage.GetProperty("supportsHttpsTrafficOnly").GetBoolean());
        Assert.Equal("TLS1_2", storage.GetProperty("minimumTlsVersion").GetString());
        Assert.Equal("None", Resources("Microsoft.Storage/storageAccounts/blobServices/containers").Single()
            .GetProperty("properties").GetProperty("publicAccess").GetString());

        var vault = Resources("Microsoft.KeyVault/vaults").Single().GetProperty("properties");
        Assert.True(vault.GetProperty("enableRbacAuthorization").GetBoolean());
        Assert.True(vault.GetProperty("enablePurgeProtection").GetBoolean());

        Assert.True(Site.GetProperty("properties").GetProperty("httpsOnly").GetBoolean());
        Assert.Equal("Disabled", SiteConfig.GetProperty("ftpsState").GetString());
        Assert.Equal("1.2", SiteConfig.GetProperty("minTlsVersion").GetString());
        Assert.True(SiteConfig.GetProperty("alwaysOn").GetBoolean());
        foreach (var name in new[] { "ftp", "scm" })
            Assert.False(Named("Microsoft.Web/sites/basicPublishingCredentialsPolicies", name).GetProperty("properties").GetProperty("allow").GetBoolean());
    }

    [Fact]
    public void TheWizardFillsEveryParameterAndEveryWizardValueReachesTheTemplate()
    {
        var parameters = Template.GetProperty("parameters").EnumerateObject().ToList();
        var outputs = Wizard.GetProperty("parameters").GetProperty("outputs").EnumerateObject().Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(parameters.Where(p => !p.Value.TryGetProperty("defaultValue", out _)), p => Assert.Contains(p.Name, outputs));
        Assert.All(outputs, o => Assert.Contains(o, parameters.Select(p => p.Name)));
        Assert.DoesNotContain("_artifactsLocation", outputs);
        Assert.DoesNotContain("_artifactsLocationSasToken", outputs);

        var elements = Wizard.GetProperty("parameters").GetProperty("steps").EnumerateArray()
            .SelectMany(s => s.GetProperty("elements").EnumerateArray()).ToDictionary(e => e.GetProperty("name").GetString()!);
        var sizes = elements["skuName"].GetProperty("constraints").GetProperty("allowedValues").EnumerateArray()
            .Select(v => v.GetProperty("value").GetString()).ToList();
        var allowed = parameters.Single(p => p.Name == "skuName").Value.GetProperty("allowedValues").EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Equal(allowed, sizes);
        Assert.Equal(parameters.Single(p => p.Name == "skuName").Value.GetProperty("defaultValue").GetString(), sizes[0]);

        string clientIdRule = elements["clientId"].GetProperty("constraints").GetProperty("regex").GetString()!;
        Assert.Matches(clientIdRule, Guid.NewGuid().ToString());
        Assert.DoesNotMatch(clientIdRule, "Hive BDC sign-in");
        Assert.DoesNotMatch(clientIdRule, Guid.NewGuid().ToString() + "x");

        string scriptLink = elements["scriptInfo"].GetProperty("options").GetProperty("uri").GetString()!;
        Assert.EndsWith("/scripts/New-HiveBDCSignInApp.ps1", scriptLink);
        Assert.True(File.Exists(Path.Combine(Root, "scripts", "New-HiveBDCSignInApp.ps1")));
    }

    [Fact]
    public void TheInstanceRunsInTheTimeZoneTheWizardAsksForAndOnlyKnownZonesCanBeChosen()
    {
        var parameter = Template.GetProperty("parameters").GetProperty("timeZone");
        var allowed = parameter.GetProperty("allowedValues").EnumerateArray().Select(v => v.GetString()!).ToList();
        Assert.Equal("Europe/Berlin", parameter.GetProperty("defaultValue").GetString());
        Assert.Equal("Europe/Berlin", allowed[0]);
        foreach (var zone in allowed)
            Assert.Equal(zone, TimeZoneInfo.FindSystemTimeZoneById(zone).Id);

        Assert.Equal("[parameters('timeZone')]", AppSettings()["TZ"]);

        var element = Wizard.GetProperty("parameters").GetProperty("steps").EnumerateArray()
            .SelectMany(s => s.GetProperty("elements").EnumerateArray()).Single(e => e.GetProperty("name").GetString() == "timeZone");
        var offered = element.GetProperty("constraints").GetProperty("allowedValues").EnumerateArray().ToList();
        Assert.Equal(allowed, offered.Select(v => v.GetProperty("value").GetString()));
        Assert.Equal(offered[0].GetProperty("label").GetString(), element.GetProperty("defaultValue").GetString());
        Assert.Equal("[steps('size').timeZone]", Wizard.GetProperty("parameters").GetProperty("outputs").GetProperty("timeZone").GetString());
    }

    [Fact]
    public void TheTemplateUsesLanguageVersionOneBecauseManagedApplicationsTakeNoOther()
    {
        bool hasVersion = Template.TryGetProperty("languageVersion", out var version);
        Assert.True(!hasVersion || version.GetString() == "1.0", "managed applications accept ARM language version 1.0 only");
        Assert.Equal(JsonValueKind.Array, Template.GetProperty("resources").ValueKind);
    }

    [Fact]
    public void TheAppStartsTheAssemblyThisProjectBuildsOnItsDotNetVersion()
    {
        var assembly = typeof(AzureRunOptions).Assembly;
        Assert.Equal($"dotnet {assembly.GetName().Name}.dll", SiteConfig.GetProperty("appCommandLine").GetString());

        string framework = assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName;
        string version = framework[(framework.LastIndexOf('v') + 1)..];
        Assert.Equal($"DOTNETCORE|{version}", SiteConfig.GetProperty("linuxFxVersion").GetString());
    }
}