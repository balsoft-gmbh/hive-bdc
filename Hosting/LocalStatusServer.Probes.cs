using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>POST /config/test-sharepoint, POST /config/test-mail and POST /license: the dashboard's probes and the licence install.</summary>
public sealed partial class LocalStatusServer
{
    // ── POST /config/test-sharepoint ────────────────────────────────────────
    // Body: { sharePoint:{ siteUrl, library, folder }, clientSecret? }. Tested with the
    // STORED shared auth (SharePointAuth); a blank posted secret reuses the stored one.
    // Reads only — never writes.

    private async Task PostTestSharePoint(HttpListenerContext ctx, HttpListenerRequest req)
    {
        TestSpDto? body;
        try { body = JsonSerializer.Deserialize<TestSpDto>(ReadBody(req), JsonOpts); }
        catch (Exception ex) { Respond(ctx, 400, new { ok = false, message = "Invalid JSON: " + ex.Message }); return; }
        body ??= new TestSpDto();
        var sp = body.SharePoint ?? new SpTargetDto();

        var cur = Snapshot();
        // Shared stored auth; a blank posted secret reuses the stored value (as-is; "enc:" is unprotected on use).
        var auth = new SharePointAuthOptions
        {
            TenantId = cur.SharePointAuth.TenantId,
            ClientId = cur.SharePointAuth.ClientId,
            ClientSecret = string.IsNullOrEmpty(body.ClientSecret) ? cur.SharePointAuth.ClientSecret : body.ClientSecret,
        };

        // The posted target is optional — resolve it only when a site+library were sent.
        SharePointTarget? target = null;
        if (!string.IsNullOrWhiteSpace(sp.SiteUrl) || !string.IsNullOrWhiteSpace(sp.Library))
        {
            var probe = new SharePointTargetOptions { SiteUrl = sp.SiteUrl ?? "", Library = sp.Library ?? "", Folder = sp.Folder ?? "" };
            target = probe.ResolveTarget(out var reason);
            if (target is null) { Respond(ctx, 200, new { ok = false, message = reason }); return; }
        }

        string targetDesc = target is null ? "auth-only (no target)" : $"{target.SiteUrl} / {target.Library}";
        _log.LogInformation("SharePoint test requested via /config/test-sharepoint — target {Target}.", targetDesc);

        var cap = new CapturingLogger();
        bool ok;
        try { ok = await SharePointUploader.TestAsync(auth, target, cap); }   // reads only; never writes
        catch (Exception ex)
        {
            _log.LogWarning("SharePoint test FAILED — {Target}: {Message}", targetDesc, ex.Message);
            Respond(ctx, 200, new { ok = false, message = ex.Message });
            return;
        }

        string summary = cap.Summary(ok);
        if (ok) _log.LogInformation("SharePoint test OK — {Target}: {Summary}", targetDesc, summary);
        else _log.LogWarning("SharePoint test FAILED — {Target}: {Summary}", targetDesc, summary);
        Respond(ctx, 200, new { ok, message = summary });
    }

    // ── POST /config/test-mail ──────────────────────────────────────────────
    // Body: { mail:{ tenantId?, clientId, clientSecret?, from, redirectTo[] } }. Acquires a
    // token and reads the sender mailbox — enough to prove the app registration works, the
    // From address exists and the app may act on it. SENDS NOTHING and writes nothing. A
    // blank clientSecret reuses the stored one, so the form can be tested before saving.

    private async Task PostTestMail(HttpListenerContext ctx, HttpListenerRequest req)
    {
        TestMailDto? body;
        try { body = JsonSerializer.Deserialize<TestMailDto>(ReadBody(req), JsonOpts); }
        catch (Exception ex) { Respond(ctx, 400, new { ok = false, message = "Invalid JSON: " + ex.Message }); return; }
        body ??= new TestMailDto();

        var cur = Snapshot();
        var posted = body.Mail;

        // Test exactly what the form shows, falling back to what is stored for anything absent.
        var probe = new MailOptions
        {
            Enabled = true,
            TenantId = posted?.TenantId ?? cur.Mail.TenantId,
            ClientId = string.IsNullOrWhiteSpace(posted?.ClientId) ? cur.Mail.ClientId : posted!.ClientId!,
            ClientSecret = string.IsNullOrWhiteSpace(posted?.ClientSecret) ? cur.Mail.ClientSecret : posted!.ClientSecret!,
            From = string.IsNullOrWhiteSpace(posted?.From) ? cur.Mail.From : posted!.From!,
            RedirectTo = posted?.RedirectTo is { Count: > 0 } r ? r : cur.Mail.RedirectTo,
        };

        // The tenant to test against: the form's, else the stored mail tenant, else SharePoint's.
        var auth = new SharePointAuthOptions
        {
            TenantId = string.IsNullOrWhiteSpace(body.TenantId) ? cur.SharePointAuth.TenantId : body.TenantId!,
        };

        _log.LogInformation("Mail test requested via /config/test-mail — sender {From}.", probe.From);

        bool ok;
        string message;
        try
        {
            (ok, message) = await MailSender.TestAsync(probe, auth, new CapturingLogger());   // reads only; sends nothing
        }
        catch (Exception ex)
        {
            _log.LogWarning("Mail test FAILED — {From}: {Message}", probe.From, ex.Message);
            Respond(ctx, 200, new { ok = false, message = ex.Message });
            return;
        }

        if (ok) _log.LogInformation("Mail test OK — {From}: {Summary}", probe.From, message);
        else _log.LogWarning("Mail test FAILED — {From}: {Summary}", probe.From, message);
        Respond(ctx, 200, new { ok, message });
    }

    // ── POST /license ───────────────────────────────────────────────────────
    // Body: { content: "<the .lic file text>", fileName? }. Pre-validates the license
    // against the embedded Balsoft cert (signature, product, tenant binding, expiry);
    // on success installs it to the effective license path (License:File, else
    // %ProgramData%\HiveBDC\license.lic) so the next Worker cycle picks it up with
    // no restart. Rejects Invalid/Expired so a bad or dead license can't overwrite a
    // working one. Nothing but the license file itself is written.

    private void PostLicense(HttpListenerContext ctx, HttpListenerRequest req)
    {
        LicenseUploadDto? dto;
        try { dto = JsonSerializer.Deserialize<LicenseUploadDto>(ReadBody(req), JsonOpts); }
        catch (Exception ex) { Respond(ctx, 400, new { ok = false, message = "Invalid JSON: " + ex.Message }); return; }

        string content = dto?.Content ?? "";
        if (string.IsNullOrWhiteSpace(content))
        { Respond(ctx, 400, new { ok = false, message = "No license content provided." }); return; }

        var opts = Snapshot();
        var result = LicenseValidator.ValidateContent(
            content, opts.SharePointAuth.TenantId, DateOnly.FromDateTime(DateTime.Today));

        // Refuse anything that wouldn't actually license the install (tampered/wrong-tenant/
        // wrong-product => Invalid; already past grace => Expired). Valid/Warning/Grace install.
        if (result.State is LicenseState.Invalid or LicenseState.Expired)
        {
            _log.LogWarning("License upload REJECTED via /license — {State}: {Message}", result.State, result.Message);
            Respond(ctx, 400, new { ok = false, state = result.State.ToString(), message = result.Message });
            return;
        }

        string target = LicenseValidator.DefaultInstallPath(opts.License.File);
        try
        {
            string dir = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(dir, $".license.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tmp, content.Trim());
            try { File.Move(tmp, target, overwrite: true); }
            catch { try { File.Delete(tmp); } catch { /* best effort */ } throw; }
        }
        catch (Exception ex)
        { Respond(ctx, 500, new { ok = false, message = "Could not install the license: " + ex.Message }); return; }

        _log.LogInformation("License installed via /license — {State}: {Message}", result.State, result.Message);
        Respond(ctx, 200, new
        {
            ok = true,
            state = result.State.ToString(),
            message = result.Message,
            customer = result.Info?.Customer,
            expires = result.ExpiresOn?.ToString("yyyy-MM-dd"),
        });
    }

    /// <summary>In-memory ILogger that captures messages so the SharePoint test can report a reason.</summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
        {
            string msg = formatter(state, ex);
            if (ex is not null) msg += " " + ex.Message;
            lock (_entries) _entries.Add((level, msg));
        }

        /// <summary>Best one-line reason: on success the last info+; on failure the last warning/error.</summary>
        public string Summary(bool ok)
        {
            lock (_entries)
            {
                if (ok)
                {
                    for (int i = _entries.Count - 1; i >= 0; i--)
                        if (_entries[i].Level >= LogLevel.Information) return _entries[i].Message;
                    return "SharePoint connection OK.";
                }
                for (int i = _entries.Count - 1; i >= 0; i--)
                    if (_entries[i].Level >= LogLevel.Warning) return _entries[i].Message;
                return _entries.Count > 0 ? _entries[^1].Message : "SharePoint test failed.";
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
