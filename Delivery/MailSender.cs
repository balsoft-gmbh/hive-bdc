using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Delivery;

/// <summary>
/// What a single document mail is addressed to, and why. Built by
/// <see cref="MailSender.PlanRecipients"/> so the decision is testable and is logged in
/// one place, rather than being buried in the send call.
/// </summary>
/// <param name="To">Recipients of the mail as it will actually be sent.</param>
/// <param name="Cc">Cc of the mail as it will actually be sent (empty when redirected).</param>
/// <param name="Redirected">True when the customer addresses were replaced by the redirect list.</param>
/// <param name="Environment">The AX environment reported by the document JSON, for the banner and the log.</param>
/// <param name="OriginalTo">The partner To addresses from the document JSON, kept for the banner.</param>
/// <param name="OriginalCc">The partner Cc addresses from the document JSON, kept for the banner.</param>
public sealed record MailPlan(
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    bool Redirected,
    string Environment,
    IReadOnlyList<string> OriginalTo,
    IReadOnlyList<string> OriginalCc);

/// <summary>The mail body as sent, with the content type Graph is told.</summary>
public sealed record MailBody(string Content, bool IsHtml);

/// <summary>Graph answered 404: the address does not name anything in the tenant.</summary>
public sealed class MailboxNotFoundException : Exception
{
    public MailboxNotFoundException(string message) : base(message) { }
}

/// <summary>
/// Sends the produced document by mail through Microsoft Graph (app-only client
/// credentials, application permission <c>Mail.Send</c>). Plain HttpClient, no Graph SDK,
/// mirroring <see cref="SharePointUploader"/>: built ONCE from the shared
/// <see cref="MailOptions"/> and reused, with the token cached for the instance lifetime.
///
/// The safety rule lives here: a document that did not come from a production AX is sent to
/// the configured redirect list and never to the customer or vendor. See <see cref="PlanRecipients"/>.
/// </summary>
public sealed partial class MailSender
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0";

    /// <summary>
    /// Above this, the attachment no longer fits in a sendMail request and a draft plus an
    /// upload session is used instead. Graph caps the whole sendMail body at 4 MB, and
    /// base64 inflates by a third, so the cut-off is deliberately conservative.
    /// </summary>
    private const int InlineAttachmentLimit = 2_500_000;

    /// <summary>Attachment upload-session chunks must be multiples of 320 KiB.</summary>
    private const int UploadChunkSize = 10 * 327_680;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly MailOptions _mail;
    private readonly string _tenantId;
    private string? _token;
    private DateTimeOffset _tokenExpiresUtc;

    public MailSender(MailOptions mail, string tenantId)
    {
        _mail = mail;
        _tenantId = tenantId;
    }

    public MailOptions Options => _mail;
    public string From => _mail.From;

    /// <summary>
    /// Sender when the mail config is usable; null otherwise (problems are logged). Returns
    /// null without complaint when mail is simply disabled.
    /// </summary>
    public static MailSender? Create(MailOptions? mail, SharePointAuthOptions? sharePointAuth, ILogger log)
    {
        if (mail is null || !mail.Enabled) return null;

        var problems = mail.Validate(sharePointAuth);
        if (problems.Count == 0)
            return new MailSender(mail, mail.ResolveTenantId(sharePointAuth));

        foreach (var p in problems)
            log.LogError("Document mail is enabled but the configuration is incomplete: {Problem}", p);
        log.LogError("Document mail is DISABLED for this run — fix the HiveBDC:Mail section in the dashboard settings.");
        return null;
    }

    /// <summary>
    /// Config smoke test (--test-mail, loopback /config/test-mail): acquire a token, then try to
    /// read the sender mailbox. Sends nothing.
    ///
    /// The token is the pass/fail part. Reading the mailbox is a bonus that needs a DIRECTORY
    /// permission the app has no reason to hold: a send-only registration carries Mail.Send and
    /// nothing else, so Graph answers 403 and that is not a fault. Only a token failure, or a
    /// mailbox that genuinely does not exist, is reported as a failure.
    /// </summary>
    public static async Task<(bool Ok, string Message)> TestAsync(
        MailOptions? mail, SharePointAuthOptions? sharePointAuth, ILogger log)
    {
        if (mail is null)
            return (false, "No HiveBDC:Mail configuration found.");

        var problems = mail.Validate(sharePointAuth);
        if (problems.Count > 0)
        {
            foreach (var p in problems) log.LogError("Mail configuration problem: {Problem}", p);
            return (false, string.Join(" ", problems));
        }

        MailSender sender;
        try
        {
            sender = new MailSender(mail, mail.ResolveTenantId(sharePointAuth));
            await sender.GetTokenAsync();
        }
        catch (Exception ex)
        {
            log.LogError("Mail test FAILED at sign-in: {Message}", ex.Message);
            return (false, "Mail test failed: " + ex.Message);
        }

        string redirect = string.Join(", ", mail.CleanRedirectTo());
        string tail = $" Non-production documents go to: {redirect}.";

        // Optional confirmation of the sender. Absence of the permission is expected.
        string? display = null;
        try
        {
            using var doc = await sender.GetJsonAsync(
                $"{GraphBase}/users/{Uri.EscapeDataString(mail.From)}?$select=displayName,mail,userPrincipalName");
            display = doc.RootElement.TryGetProperty("displayName", out var d) ? d.GetString() : null;
        }
        catch (MailboxNotFoundException)
        {
            log.LogError("Mail test FAILED: the mailbox {From} does not exist in tenant {Tenant}.", mail.From, sender._tenantId);
            return (false, $"Sign-in worked, but the mailbox {mail.From} does not exist in this tenant. " +
                           "Check the address under 'Send from'.");
        }
        catch (Exception ex)
        {
            log.LogInformation(
                "Mail test: signed in fine; the sender mailbox could not be read ({Message}). " +
                "That is expected for a send-only app registration, which holds Mail.Send and no directory permission.",
                ex.Message);
            log.LogInformation("Mail test OK — credentials accepted for tenant {Tenant}.", sender._tenantId);
            return (true, $"Mail OK. Signed in and ready to send as {mail.From}. " +
                          "(The mailbox itself could not be read back, which is normal: the app has permission to " +
                          "send mail, not to read the directory.)" + tail);
        }

        log.LogInformation("Mail test OK — sender {From} resolved in tenant {Tenant}.", mail.From, sender._tenantId);
        return (true, $"Mail OK. Sending as {mail.From}" +
                      (string.IsNullOrWhiteSpace(display) ? "" : $" ({display})") + "." + tail);
    }

    /// <summary>
    /// Decide where this document's mail actually goes.
    ///
    /// Production document: the partner addresses out of the JSON. Anything else,
    /// including a document whose JSON carries no environment at all, goes to the configured
    /// redirect list instead, because an export that cannot prove it came from production
    /// must never reach a customer or vendor.
    /// </summary>
    public MailPlan PlanRecipients(string? environment, string? recipientEmail, string? recipientEmailCc)
    {
        var originalTo = MailAddresses.Split(recipientEmail);
        var originalCc = MailAddresses.Split(recipientEmailCc);
        string env = SourceEnvironment.Describe(environment);

        if (SourceEnvironment.IsLive(environment))
            return new MailPlan(originalTo, originalCc, false, env, originalTo, originalCc);

        return new MailPlan(_mail.CleanRedirectTo(), Array.Empty<string>(), true, env, originalTo, originalCc);
    }

    /// <summary>Subject line as sent: the ERP subject with %1 filled in, prefixed when redirected.</summary>
    public static string BuildSubject(string? erpSubject, string documentLabel, string documentNumber, MailPlan plan)
    {
        // Lasernet writes its own placeholder into the destination subject, e.g.
        // "Rechnung Nr. %1". AX does not expand it, so the service does.
        string subject = (erpSubject ?? "").Replace("%1", documentNumber).Trim();
        if (subject.Length == 0)
            subject = $"{documentLabel} {documentNumber}";

        return plan.Redirected ? $"[{plan.Environment}] {subject}" : subject;
    }

    /// <summary>
    /// Whether the ERP body is HTML rather than plain text. Lasernet destinations often
    /// hold Word-generated HTML (MsoNormal paragraphs, FONT tags); sending that as text
    /// showed the raw tags in Outlook. A body with any common markup tag is treated as HTML.
    /// </summary>
    public static bool LooksLikeHtml(string? body)
        => !string.IsNullOrWhiteSpace(body)
           && HtmlTag().IsMatch(body);

    [System.Text.RegularExpressions.GeneratedRegex(@"<\s*(html|body|p|div|span|br|table|tr|td|font|a|b|i|u|strong|em|ul|ol|li|h[1-6])\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex HtmlTag();

    /// <summary>
    /// Body as sent: the ERP body (kept as HTML when it is HTML), with a banner on top when the
    /// mail was redirected. For HTML the banner is inserted right after &lt;body&gt; (or prepended
    /// when there is no body tag) so it renders inside the document rather than as text before it.
    /// A redirected plain-text body (some Lasernet destinations store the
    /// mail text as plain text) is converted to simple HTML first, so every test mail shows the
    /// same highlighted banner. Mails that reach the customer are never converted: their body
    /// goes out exactly as the ERP wrote it.
    /// </summary>
    public static MailBody BuildBody(string? erpBody, string documentLabel, string documentNumber, string partyLabel, MailPlan plan)
    {
        string body = erpBody ?? "";
        bool html = LooksLikeHtml(body);
        if (!plan.Redirected) return new MailBody(body, html);

        string original = plan.OriginalTo.Count > 0 ? string.Join(", ", plan.OriginalTo) : "(no recipient in the document)";
        string cc = plan.OriginalCc.Count > 0 ? string.Join(", ", plan.OriginalCc) : "";

        static string E(string s) => System.Net.WebUtility.HtmlEncode(s);
        if (!html) body = TextToHtml(body);
        var block = new StringBuilder();
        block.Append("<div style=\"border:2px solid #B45309;background:#FEF3C7;color:#78350F;padding:10px 12px;margin:0 0 14px 0;font-family:Segoe UI,Arial,sans-serif;font-size:13px\">");
        block.Append($"<b>{E(plan.Environment.ToUpperInvariant())} SYSTEM</b> — this {E(documentLabel)} mail was redirected and did NOT go to the {E(partyLabel)}.<br/>");
        block.Append($"{E(documentLabel)}: {E(documentNumber)}<br/>");
        block.Append($"Would have gone to: {E(original)}");
        if (cc.Length > 0) block.Append($"<br/>Copy to: {E(cc)}");
        block.Append("</div>");

        var bodyTag = BodyTag().Match(body);
        string content = bodyTag.Success
            ? body.Insert(bodyTag.Index + bodyTag.Length, block.ToString())
            : block + body;
        return new MailBody(content, true);
    }

    /// <summary>
    /// Plain text as HTML that reads the same: every character escaped, each line break a
    /// &lt;br&gt;, in the font Outlook uses for mail text.
    /// </summary>
    public static string TextToHtml(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => System.Net.WebUtility.HtmlEncode(l));
        return "<html><body><div style=\"font-family:Calibri,Arial,sans-serif;font-size:11pt\">"
            + string.Join("<br/>\n", lines)
            + "</div></body></html>";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"<\s*body\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex BodyTag();

    public void Send(MailPlan plan, string subject, MailBody body, string attachmentPath)
        => SendAsync(plan, subject, body, attachmentPath).GetAwaiter().GetResult();

    /// <summary>
    /// Send one document mail with the produced file attached. Throws on any Graph
    /// failure, so the caller can route the pair to error\ and retry it later rather than
    /// recording an export that never reached anyone.
    /// </summary>
    public async Task SendAsync(MailPlan plan, string subject, MailBody body, string attachmentPath)
    {
        if (plan.To.Count == 0)
            throw new InvalidOperationException("The mail has no recipients.");
        if (!File.Exists(attachmentPath))
            throw new FileNotFoundException($"Attachment '{attachmentPath}' does not exist.", attachmentPath);

        byte[] bytes = await File.ReadAllBytesAsync(attachmentPath);
        string fileName = Path.GetFileName(attachmentPath);
        string contentType = ContentTypeFor(fileName);

        if (bytes.Length <= InlineAttachmentLimit)
            await SendInlineAsync(plan, subject, body, fileName, contentType, bytes);
        else
            await SendViaDraftAsync(plan, subject, body, fileName, contentType, bytes);
    }

    // ── Graph plumbing ──────────────────────────────────────────────────────

    /// <summary>Small attachment: one sendMail call carrying the file base64-encoded.</summary>
    private async Task SendInlineAsync(
        MailPlan plan, string subject, MailBody body, string fileName, string contentType, byte[] bytes)
    {
        var message = BuildMessage(plan, subject, body);
        message["attachments"] = new JsonArray
        {
            new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.fileAttachment",
                ["name"] = fileName,
                ["contentType"] = contentType,
                ["contentBytes"] = Convert.ToBase64String(bytes),
            },
        };

        var payload = new JsonObject
        {
            ["message"] = message,
            ["saveToSentItems"] = _mail.SaveToSentItems,
        };

        using var req = await NewRequestAsync(HttpMethod.Post, $"{GraphBase}/users/{Uri.EscapeDataString(_mail.From)}/sendMail");
        req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"sendMail failed ({(int)resp.StatusCode}): {ErrorSummary(await resp.Content.ReadAsStringAsync())}");
    }

    /// <summary>
    /// Large attachment: create a draft, stream the file into it with an upload session, then
    /// send the draft. sendMail cannot carry it, because Graph caps that request at 4 MB.
    /// </summary>
    private async Task SendViaDraftAsync(
        MailPlan plan, string subject, MailBody body, string fileName, string contentType, byte[] bytes)
    {
        string user = Uri.EscapeDataString(_mail.From);

        using var createReq = await NewRequestAsync(HttpMethod.Post, $"{GraphBase}/users/{user}/messages");
        createReq.Content = new StringContent(BuildMessage(plan, subject, body).ToJsonString(), Encoding.UTF8, "application/json");
        using var createResp = await Http.SendAsync(createReq);
        string createJson = await createResp.Content.ReadAsStringAsync();
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Creating the draft failed ({(int)createResp.StatusCode}): {ErrorSummary(createJson)}");

        string messageId;
        using (var doc = JsonDocument.Parse(createJson))
            messageId = doc.RootElement.GetProperty("id").GetString()!;

        await UploadAttachmentAsync(user, messageId, fileName, contentType, bytes);

        using var sendReq = await NewRequestAsync(HttpMethod.Post, $"{GraphBase}/users/{user}/messages/{messageId}/send");
        sendReq.Content = new StringContent("", Encoding.UTF8, "application/json");
        using var sendResp = await Http.SendAsync(sendReq);
        if (!sendResp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Sending the draft failed ({(int)sendResp.StatusCode}): {ErrorSummary(await sendResp.Content.ReadAsStringAsync())}");
    }

    private async Task UploadAttachmentAsync(string user, string messageId, string fileName, string contentType, byte[] bytes)
    {
        var sessionPayload = new JsonObject
        {
            ["AttachmentItem"] = new JsonObject
            {
                ["attachmentType"] = "file",
                ["name"] = fileName,
                ["size"] = bytes.Length,
                ["contentType"] = contentType,
            },
        };

        using var createReq = await NewRequestAsync(
            HttpMethod.Post, $"{GraphBase}/users/{user}/messages/{messageId}/attachments/createUploadSession");
        createReq.Content = new StringContent(sessionPayload.ToJsonString(), Encoding.UTF8, "application/json");
        using var createResp = await Http.SendAsync(createReq);
        string createJson = await createResp.Content.ReadAsStringAsync();
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"createUploadSession for the attachment failed ({(int)createResp.StatusCode}): {ErrorSummary(createJson)}");

        string uploadUrl;
        using (var doc = JsonDocument.Parse(createJson))
            uploadUrl = doc.RootElement.GetProperty("uploadUrl").GetString()!;

        for (long offset = 0; offset < bytes.Length; offset += UploadChunkSize)
        {
            int size = (int)Math.Min(UploadChunkSize, bytes.Length - offset);
            using var chunkReq = new HttpRequestMessage(HttpMethod.Put, uploadUrl);   // pre-authenticated URL, no bearer
            chunkReq.Content = new ByteArrayContent(bytes, (int)offset, size);
            chunkReq.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + size - 1, bytes.Length);
            using var chunkResp = await Http.SendAsync(chunkReq);
            if (!chunkResp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Attachment chunk upload failed ({(int)chunkResp.StatusCode}): {ErrorSummary(await chunkResp.Content.ReadAsStringAsync())}");
        }
    }

    private static JsonObject BuildMessage(MailPlan plan, string subject, MailBody body)
    {
        var message = new JsonObject
        {
            ["subject"] = subject,
            ["body"] = new JsonObject
            {
                // Plain text keeps the ERP's line breaks as they are; HTML (what Lasernet holds
                // when the destination text was authored in Word) is passed through as HTML.
                ["contentType"] = body.IsHtml ? "HTML" : "Text",
                ["content"] = body.Content,
            },
            ["toRecipients"] = Recipients(plan.To),
        };
        if (plan.Cc.Count > 0)
            message["ccRecipients"] = Recipients(plan.Cc);
        return message;
    }

    private static JsonArray Recipients(IEnumerable<string> addresses)
    {
        var arr = new JsonArray();
        foreach (var a in addresses)
            arr.Add(new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = a } });
        return arr;
    }

    private static string ContentTypeFor(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".xml" => "application/xml",
            _ => "application/octet-stream",
        };

    private async Task<string> GetTokenAsync()
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresUtc)
            return _token;

        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _mail.ClientId,
            ["client_secret"] = SecretProtector.Unprotect(_mail.ClientSecret),
            ["scope"] = "https://graph.microsoft.com/.default",
            ["grant_type"] = "client_credentials",
        });
        using var resp = await Http.PostAsync(
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(_tenantId)}/oauth2/v2.0/token", body);
        string json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Mail token request failed ({(int)resp.StatusCode}): {ErrorSummary(json)}");

        using var doc = JsonDocument.Parse(json);
        _token = doc.RootElement.GetProperty("access_token").GetString()
                 ?? throw new InvalidOperationException("Token response contained no access_token.");
        int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        _tokenExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 300));
        return _token;
    }

    private async Task<JsonDocument> GetJsonAsync(string url)
    {
        using var req = await NewRequestAsync(HttpMethod.Get, url);
        using var resp = await Http.SendAsync(req);
        string json = await resp.Content.ReadAsStringAsync();
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new MailboxNotFoundException(ErrorSummary(json));
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GET {url} failed ({(int)resp.StatusCode}): {ErrorSummary(json)}");
        return JsonDocument.Parse(json);
    }

    private async Task<HttpRequestMessage> NewRequestAsync(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync());
        return req;
    }

    /// <summary>Compact one-line error from a Graph/AAD error response body (never includes secrets).</summary>
    private static string ErrorSummary(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object)
                    return $"{err.GetProperty("code").GetString()}: {err.GetProperty("message").GetString()}";
                if (err.ValueKind == JsonValueKind.String)
                {
                    string desc = doc.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() ?? "" : "";
                    return $"{err.GetString()}: {desc.Split('\n')[0]}";
                }
            }
        }
        catch { /* not JSON */ }
        return body.Length > 300 ? body[..300] : body;
    }
}
