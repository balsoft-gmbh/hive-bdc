using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>
/// Long-running background loop: scan the input folder, wait PollSeconds, repeat.
/// Hosted by the generic host so the same exe runs as a console app (Ctrl+C) or
/// a Windows service (SCM stop) without code changes.
///
/// Config is read HOT via <see cref="IOptionsMonitor{T}"/>: every cycle re-reads
/// <c>CurrentValue</c>, so edits to appsettings.json (by hand or via the loopback
/// /config API) take effect without a service restart. When the SharePoint
/// sub-config changes between cycles the <see cref="SharePointUploader"/> is
/// re-created so a new site/library/credentials/field-map is honored immediately.
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly IOptionsMonitor<BDCOptions> _optsMonitor;
    private readonly ILogger<Worker> _log;
    private readonly ExportLedger _ledger;
    private readonly DocumentProcessor _processor;
    private LicenseState? _lastLicenseState;
    private int _lastDocumentTypeCount = -1;

    // Rate-limit the "idle" heartbeat so an inactive service still proves it is alive
    // (and shows WHICH folders it watches) without one line every poll interval.
    private DateTime _lastIdleHeartbeatUtc = DateTime.MinValue;
    private static readonly TimeSpan IdleHeartbeat = TimeSpan.FromMinutes(5);
    private DateTime _lastLogCleanupUtc = DateTime.MinValue;
    private DateTime _lastLicenseHoldLogUtc = DateTime.MinValue;

    public Worker(IOptionsMonitor<BDCOptions> opts, ILogger<Worker> log, ExportLedger ledger, DocumentTypeRegistry registry)
    {
        _optsMonitor = opts;
        _log = log;
        _ledger = ledger;
        _processor = new DocumentProcessor(log, registry, ledger);
    }

    /// <summary>A snapshot of the current options, normalized (idempotent) for use this cycle.</summary>
    private BDCOptions Current()
    {
        var o = _optsMonitor.CurrentValue;
        o.Normalize();
        return o;
    }

    /// <summary>
    /// A stable, non-reversible marker for a stored secret, so a signature can notice the
    /// secret changed without ever carrying the value itself.
    /// </summary>
    private static string SecretFingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "none";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0, 8);
    }

    /// <summary>
    /// Cheap change signature of the SharePoint config (shared auth + every company's
    /// target/fields/enabled). When it differs between cycles the uploader is rebuilt.
    ///
    /// The secret is included as a FINGERPRINT, not skipped: the uploader captures the options
    /// object it was built from, so a corrected secret would otherwise never reach it and the
    /// service would keep authenticating with the old one until it was restarted.
    /// </summary>
    private static string SharePointSignature(BDCOptions o)
    {
        var a = o.SharePointAuth;
        var parts = new List<string> { a.TenantId, a.ClientId, SecretFingerprint(a.ClientSecret) };
        foreach (var t in o.DocumentTypes)
            foreach (var r in t.Companies)
            {
                var sp = r.SharePoint;
                string fields = string.Join(";",
                    sp.Fields.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value));
                parts.Add(string.Join("~", t.Id, r.DataAreaId, sp.Enabled, sp.SiteUrl, sp.Library, sp.Folder, fields));
            }
        return string.Join("|", parts);
    }

    /// <summary>Whether any company entry has SharePoint upload enabled (=> a shared uploader is needed).</summary>
    private static bool AnySharePointEnabled(BDCOptions o) => o.AnySharePointEnabled();

    /// <summary>
    /// Cheap change signature of the mail config, including a fingerprint of the secret so
    /// that correcting only the secret rebuilds the sender.
    /// </summary>
    private static string MailSignature(BDCOptions o)
    {
        var m = o.Mail;
        return string.Join("~", m.Enabled, m.ResolveTenantId(o.SharePointAuth), m.ClientId, m.From,
            string.Join(";", m.CleanRedirectTo()), m.SaveToSentItems, SecretFingerprint(m.ClientSecret));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = Current();

        // One shared uploader (built from the shared auth) serves every company; its
        // per-site drive cache handles multiple sites/libraries. null when no company
        // enables SharePoint or the shared auth is unusable.
        var uploader = AnySharePointEnabled(opts) ? SharePointUploader.Create(opts.SharePointAuth, _log) : null;
        string spSignature = SharePointSignature(opts);

        // One shared mail sender (own app registration, SharePoint tenant by default) serves
        // every company. null when mail is disabled or the mail config is unusable.
        var mailer = MailSender.Create(opts.Mail, opts.SharePointAuth, _log);
        string mailSignature = MailSignature(opts);

        _log.LogInformation(
            "Hive BDC service started. companies={Companies} documentTypes={Types} poll={Poll}s sharepoint={SharePoint}",
            opts.CompanyIds().Count, opts.DocumentTypes.Count, opts.PollSeconds,
            uploader is null ? "off" : opts.AllRoutes().Count(r => r.SharePoint.Enabled) + " entr(y/ies) on");

        LogMailConfig(opts, mailer);
        LogDocumentTypes(opts);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Hot config: re-read every cycle so appsettings.json edits (hand or /config API)
                // are picked up without a restart.
                opts = Current();

                // Re-create the SharePoint uploader when the auth/company config changed since last cycle.
                string sig = SharePointSignature(opts);
                if (sig != spSignature)
                {
                    uploader = AnySharePointEnabled(opts) ? SharePointUploader.Create(opts.SharePointAuth, _log) : null;
                    spSignature = sig;
                    _log.LogInformation(
                        "SharePoint configuration changed — export is now {State}.",
                        uploader is null ? "OFF" : "ON (" + opts.AllRoutes().Count(r => r.SharePoint.Enabled) + " entr(y/ies))");
                }

                // Same for mail: rebuild the sender when the mail config changed since last cycle.
                string mailSig = MailSignature(opts);
                if (mailSig != mailSignature)
                {
                    mailer = MailSender.Create(opts.Mail, opts.SharePointAuth, _log);
                    mailSignature = mailSig;
                    _log.LogInformation("Document mail configuration changed.");
                    LogMailConfig(opts, mailer);
                }

                // A new type (from Settings, or auto-registered by the processor) is worth one line.
                if (opts.DocumentTypes.Count != _lastDocumentTypeCount)
                    LogDocumentTypes(opts);

                // Daily housekeeping: drop ledger rows older than the retention window so
                // hive.db stays bounded (self-throttles to once/24h; lifetime total kept).
                _ledger.PruneIfDue();
                DeleteOldLogsIfDue(opts);

                // Re-check the license every cycle (cheap) so expiry/renewal is picked up
                // without a service restart. Log only when the state changes to avoid spam.
                var license = LicenseValidator.Validate(
                    opts.License.File, opts.SharePointAuth.TenantId, DateOnly.FromDateTime(DateTime.Today));
                if (license.State != _lastLicenseState)
                {
                    LicenseLogging.Report(_log, license);
                    _lastLicenseState = license.State;
                }

                if (license.Blocked)
                {
                    // Do NOT process while unlicensed — documents accumulate in the input
                    // folder (nothing is lost) and resume automatically once a valid
                    // license is installed.
                    // Visible once an hour, so a log read days later still explains why nothing moved.
                    if (DateTime.UtcNow - _lastLicenseHoldLogUtc >= TimeSpan.FromHours(1))
                    {
                        _lastLicenseHoldLogUtc = DateTime.UtcNow;
                        _log.LogWarning("cycle: nothing processed, the license is {State}. Documents wait in the input folders and are processed once a valid license is installed.", license.State);
                    }
                    else
                        _log.LogDebug("cycle: skipped (license {State})", license.State);
                }
                else
                {
                    // Scan every company's input folder. A per-company uploader is passed
                    // only when that company has SharePoint enabled.
                    var total = _processor.ScanOnce(opts, license, uploader, mailer);
                    if (total.Processed > 0 || total.Errors > 0 || total.Held > 0)
                        _log.LogInformation("cycle: processed={Processed} errors={Errors} held={Held}", total.Processed, total.Errors, total.Held);
                    else
                        IdleHeartbeatTick(opts);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "scan cycle failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, opts.PollSeconds)), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // shutting down
            }
        }

        _log.LogInformation("Hive BDC service stopping.");
    }

    /// <summary>
    /// Once a day (and at the first cycle): delete log files older than
    /// <see cref="LogSetup.RetentionDays"/> days, so the log folder does not grow forever.
    /// </summary>
    private void DeleteOldLogsIfDue(BDCOptions opts)
    {
        if (DateTime.UtcNow - _lastLogCleanupUtc < TimeSpan.FromHours(24)) return;
        _lastLogCleanupUtc = DateTime.UtcNow;
        string? dir = LogSetup.ResolveLogDir(opts.LogDir);
        var (deleted, problems) = LogSetup.DeleteOldLogs(dir);
        if (deleted > 0)
            _log.LogInformation("Log cleanup: deleted {Count} log file(s) older than {Days} days in {Dir}.", deleted, LogSetup.RetentionDays, dir);
        foreach (var p in problems)
            _log.LogWarning("Log cleanup: {Problem}", p);
    }

    /// <summary>
    /// One line per document type plus one per company entry (folder, exists?, output,
    /// SharePoint target). The first thing to check when "nothing happens": an empty log
    /// usually means the watched path is not where the ERP drops files.
    /// </summary>
    private void LogDocumentTypes(BDCOptions opts)
    {
        _lastDocumentTypeCount = opts.DocumentTypes.Count;
        foreach (var t in opts.DocumentTypes)
        {
            _log.LogInformation("document type {Type}: {Party}, {Handler}{Flags}, {Count} company entr(y/ies)",
                t.Id, t.Party, t.Handler,
                (t.Enabled ? "" : ", DISABLED") + (t.Mail ? "" : ", no mail") + (t.Discovered ? ", auto-registered" : ""),
                t.Companies.Count);
            foreach (var r in t.Companies)
            {
                var sp = r.SharePoint;
                string spDesc = !sp.Enabled ? "off"
                    : sp.HasTarget ? $"{sp.SiteUrl} / {sp.Library}" + (string.IsNullOrWhiteSpace(sp.Folder) ? "" : " / " + sp.Folder)
                    : "ON but NO site/library";
                _log.LogInformation("  [{Type}/{Area}] input={Input} (exists={Exists}); output={Output}; sharepoint={SharePoint}",
                    t.Id, r.DataAreaId, r.Input, Directory.Exists(r.Input), r.OutputDirFor(t), spDesc);
            }
        }
    }

    /// <summary>
    /// Log the effective mail config, so the log answers "why did/didn't this document get mailed"
    /// without anyone opening appsettings.json. The secret is never named, only its presence.
    /// </summary>
    private void LogMailConfig(BDCOptions opts, MailSender? mailer)
    {
        var m = opts.Mail;
        if (!m.Enabled)
        {
            _log.LogInformation("document mail: OFF (not enabled in settings).");
            return;
        }
        if (mailer is null)
        {
            _log.LogWarning("document mail: ENABLED but unusable — see the configuration problems above. Nothing will be mailed.");
            return;
        }

        _log.LogInformation(
            "document mail: ON — sending as {From} (tenant {Tenant}, client {Client}); " +
            "production documents go to the customer/vendor, everything else to {Redirect}.",
            m.From, m.ResolveTenantId(opts.SharePointAuth), m.ClientId, string.Join(", ", m.CleanRedirectTo()));
    }

    /// <summary>
    /// Idle cycle: emit an Information heartbeat at most once every <see cref="IdleHeartbeat"/>
    /// (so a quiet service still proves it is alive and shows WHICH folders it watches), and a
    /// Debug line every cycle in between.
    /// </summary>
    private void IdleHeartbeatTick(BDCOptions opts)
    {
        var now = DateTime.UtcNow;
        if (now - _lastIdleHeartbeatUtc >= IdleHeartbeat)
        {
            _lastIdleHeartbeatUtc = now;
            _log.LogInformation(
                "idle — no new documents in the last cycle. Watching {Count} folder(s): {Folders}",
                opts.ScanFolders().Count,
                string.Join(" | ", opts.ScanFolders().Select(f => $"[{f.ImpliedTypeId ?? "shared"}] {f.Path}")));
        }
        else
        {
            _log.LogDebug("cycle: idle");
        }
    }
}
