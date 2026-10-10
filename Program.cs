using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

// ── CLI ─────────────────────────────────────────────────────────────────────
//   Service (default):  HiveBDC [--input <dir>] [--output <dir>] [--poll <sec>]
//   Console one-shot:    HiveBDC --once   [same overrides]
//   Utilities:           HiveBDC --make-sample      (write one sample per built-in document type)
//                        HiveBDC --dump-embedded <zugferd.pdf>
//                        HiveBDC --test-sharepoint   (verify SharePoint AUTH and the first configured company target)
//                        HiveBDC --test-mail         (verify the mail app registration and sender mailbox; sends nothing)
//                        HiveBDC --test-license      (verify the license file: signature, tenant binding, expiry)
//   Service setup:       HiveBDC install   [--dry-run]   (elevated, interactive)
//                        HiveBDC uninstall                (elevated, interactive)
//   Unattended setup:    HiveBDC install --quiet [--input <dir>] [--output <dir>]
//                          [--poll <sec>] [--logdir <dir>] [--account <DOMAIN\user>]
//                          [--password <pw>] [--service-name <n>] [--display-name <n>]
//                          [--start|--no-start]              (must already be elevated)
//                        HiveBDC uninstall --quiet [--service-name <n>]
//
// Config precedence (low->high): appsettings.json -> env (HiveBDC__*) -> CLI overrides.
// A "HiveDocFlow" section (appsettings.json copied over from Hive DocFlow) is honored with a
// warning and migrated on the first dashboard save.

bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

bool makeSample = Has("--make-sample");
bool once = Has("--once");
bool testSharePoint = Has("--test-sharepoint");
bool testMail = Has("--test-mail");
bool testLicense = Has("--test-license");

// ── Service install / uninstall (no host needed; handled up front) ──────────
// Setup mode: if this exe is named like an installer (e.g. HiveBDC-Setup.exe)
// and launched with no command (double-click), default to the interactive install —
// but NEVER when started by the Service Control Manager (it must run as the service).
string procName = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
bool looksLikeSetup = procName.Contains("setup", StringComparison.OrdinalIgnoreCase)
                   || procName.Contains("install", StringComparison.OrdinalIgnoreCase);
bool anyCommand = Has("install") || Has("--install") || Has("uninstall") || Has("--uninstall")
               || once || makeSample || Has("--dump-embedded") || testSharePoint || testLicense;
if (looksLikeSetup && !anyCommand && !WindowsServiceHelpers.IsWindowsService())
    return Installer.RunInstall(args);

if (Has("install") || Has("--install")) return Installer.RunInstall(args);
if (Has("uninstall") || Has("--uninstall")) return Installer.RunUninstall(args);

string? dumpTarget = null;
var dumpIdx = Array.FindIndex(args, a => a.Equals("--dump-embedded", StringComparison.OrdinalIgnoreCase));
if (dumpIdx >= 0 && dumpIdx + 1 < args.Length)
    dumpTarget = args[dumpIdx + 1];

// Flags / non-config args are stripped before handing the rest to the command-line
// config provider (which only understands the --input/--output/--poll value options).
var flagSet = new[] { "--once", "--make-sample", "--watch", "-w", "--test-sharepoint", "--test-mail", "--test-license" };
var configArgs = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    var a = args[i];
    if (flagSet.Contains(a, StringComparer.OrdinalIgnoreCase)) continue;
    if (a.Equals("--dump-embedded", StringComparison.OrdinalIgnoreCase)) { i++; continue; } // skip its value
    configArgs.Add(a);
}

var settings = new HostApplicationBuilderSettings
{
    Args = Array.Empty<string>(),                  // we add our own (filtered) command line below
    ContentRootPath = AppContext.BaseDirectory,    // so appsettings.json resolves when run as a service
};
var builder = Host.CreateApplicationBuilder(settings);

// Config section: "HiveBDC", falling back to a "HiveDocFlow" section so an appsettings.json
// copied over from Hive DocFlow keeps its configured paths.
string sectionName = builder.Configuration.GetSection(SettingsFile.Section).Exists() ? SettingsFile.Section
    : builder.Configuration.GetSection(SettingsFile.LegacySection).Exists() ? SettingsFile.LegacySection
    : SettingsFile.Section;
bool legacySection = sectionName == SettingsFile.LegacySection;

var switchMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["--input"] = $"{sectionName}:Input",
    ["-i"] = $"{sectionName}:Input",
    ["--output"] = $"{sectionName}:Output",
    ["-o"] = $"{sectionName}:Output",
    ["--poll"] = $"{sectionName}:PollSeconds",
    ["-p"] = $"{sectionName}:PollSeconds",
};
builder.Configuration.AddCommandLine(configArgs.ToArray(), switchMappings);

// appsettings.json is loaded by Host.CreateApplicationBuilder with reloadOnChange:true
// by default (from ContentRootPath), so IOptionsMonitor<BDCOptions> reflects
// on-disk edits — including atomic writes from the loopback POST /config endpoint —
// without a service restart. The section binding below participates in that reload.
builder.Services.Configure<BDCOptions>(builder.Configuration.GetSection(sectionName));
builder.Services.AddWindowsService(o => o.ServiceName = "HiveBDC");
builder.Services.AddSingleton(sp =>
    ExportLedger.CreateIfMissing(sp.GetRequiredService<ILoggerFactory>().CreateLogger("ExportLedger")));
string settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
builder.Services.AddSingleton(sp =>
    new DocumentTypeRegistry(settingsPath, sp.GetRequiredService<ILoggerFactory>().CreateLogger("DocumentTypes")));
builder.Services.AddHostedService<Worker>();

// ── Logging: route everything through Serilog (console + rolling daily file) ─
string? resolvedLogDir = LogSetup.ResolveLogDir(builder.Configuration[$"{sectionName}:LogDir"]);
var serilogLogger = LogSetup.Build(
    resolvedLogDir,
    LogSetup.ParseLevel(builder.Configuration["Logging:LogLevel:Default"]));
builder.Logging.ClearProviders();              // drop the default console/eventsource/eventlog
builder.Logging.AddSerilog(serilogLogger, dispose: true);
if (OperatingSystem.IsWindows())
{
#pragma warning disable CA1416 // runtime-guarded above; EventLog is Windows-only by design
    builder.Logging.AddEventLog(o => o.SourceName = "HiveBDC");  // keep Event Log when run as a service
#pragma warning restore CA1416
}

// Loopback config + status API for the companion desktop app. Reads are observational;
// POST /config writes appsettings.json atomically (the file-watcher then hot-reloads it).
var appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
builder.Services.AddSingleton<IHostedService>(sp => new LocalStatusServer(
    sp.GetRequiredService<IOptionsMonitor<BDCOptions>>(),
    sp.GetRequiredService<ExportLedger>(),
    sp.GetRequiredService<ILogger<LocalStatusServer>>(),
    resolvedLogDir, appVersion, settingsPath));

using var host = builder.Build();

var opts = host.Services.GetRequiredService<IOptions<BDCOptions>>().Value;
opts.MigrateLegacy();   // fold an old single-config appsettings into SharePointAuth + Companies
opts.Normalize();
var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HiveBDC");

// Startup banner: shows the effective config (incl. where the log file lives).
log.LogInformation(
    "Hive BDC, by Balsoft v{Version} — companies={Companies} [{Areas}] documentTypes={Types} folders={Folders} poll={Poll}s logDir={LogDir}",
    appVersion, opts.CompanyIds().Count, string.Join(", ", opts.CompanyIds()),
    opts.DocumentTypes.Count, opts.ScanFolders().Count, opts.PollSeconds, resolvedLogDir ?? "(console only)");
if (legacySection)
    log.LogWarning(
        "Settings were read from the Hive DocFlow section \"HiveDocFlow\". Rename it to \"HiveBDC\" " +
        "in appsettings.json, or save the settings once from the dashboard, which migrates it.");

// ── License (grace-and-warn; bound to the customer's M365 tenant) ───────────
// The service Worker re-checks this each cycle; here we log the startup status
// and handle --test-license / --once. A valid license is always required — a
// missing/invalid/expired-past-grace license stops processing.
var license = LicenseValidator.Validate(opts.License.File, opts.SharePointAuth.TenantId, DateOnly.FromDateTime(DateTime.Today));
LicenseLogging.Report(log, license);
if (testLicense)
    return license.Blocked ? 1 : 0;

// ── Console utilities / one-shot (run and exit, no service) ─────────────────
if (makeSample)
{
    // Samples go to the sales invoice's first company folder, else the first folder of any
    // type (a shared drop folder is the usual single-company layout).
    string sampleInput = DocumentTypeRegistry.Find(opts, DocumentTypeCatalog.DefaultId)?.Companies.FirstOrDefault()?.Input
                         ?? opts.AllRoutes().FirstOrDefault()?.Input
                         ?? @"C:\Temp\HiveBDC";
    SampleData.Write(sampleInput);
    log.LogInformation("Sample documents written to {Input}", sampleInput);
    return 0;
}

if (dumpTarget is not null)
{
    Console.Write(EInvoiceGenerator.ExtractEmbeddedXml(dumpTarget));
    return 0;
}

if (testSharePoint)
{
    // Test the shared auth, resolving the first SharePoint-enabled company entry's target if there is one.
    var testTarget = opts.AllRoutes()
        .Where(r => r.SharePoint.Enabled)
        .Select(r => r.SharePoint.ResolveTarget(out _))
        .FirstOrDefault(t => t is not null);
    return await SharePointUploader.TestAsync(opts.SharePointAuth, testTarget, log) ? 0 : 1;
}

if (testMail)
{
    var (mailOk, mailMessage) = await MailSender.TestAsync(opts.Mail, opts.SharePointAuth, log);
    log.LogInformation("{Message}", mailMessage);
    return mailOk ? 0 : 1;
}

if (once)
{
    if (license.Blocked)
    {
        log.LogCritical("One-shot scan skipped — no valid license.");
        return 2;
    }
    log.LogInformation("One-shot scan. folders={Folders} documentTypes={Types}", opts.ScanFolders().Count, opts.DocumentTypes.Count);
    var ledger = host.Services.GetRequiredService<ExportLedger>();
    var registry = host.Services.GetRequiredService<DocumentTypeRegistry>();
    var uploader = opts.AnySharePointEnabled() ? SharePointUploader.Create(opts.SharePointAuth, log) : null;
    var mailer = MailSender.Create(opts.Mail, opts.SharePointAuth, log);
    var processor = new DocumentProcessor(log, registry, ledger);
    var total = processor.ScanOnce(opts, license, uploader, mailer);
    log.LogInformation("Done. processed={Processed} errors={Errors} held={Held}", total.Processed, total.Errors, total.Held);
    return total.Errors > 0 ? 1 : 0;
}

// ── Service / watch host (runs until Ctrl+C or SCM stop) ────────────────────
if (!WindowsServiceHelpers.IsWindowsService())
    log.LogInformation("Running as console (press Ctrl+C to stop). Poll every {Poll}s.", opts.PollSeconds);

try
{
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    // Make a startup/host crash visible in the log file + console instead of vanishing.
    log.LogCritical(ex, "Host terminated unexpectedly.");
    return 1;
}
