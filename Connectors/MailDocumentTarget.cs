using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Connectors;

/// <summary>
/// Mails the produced file, when mail is configured and the document names a recipient.
///
/// The environment stamped by the ERP decides who receives it: a production document goes
/// to the customer/vendor, anything else (including a document whose JSON carries no
/// environment) goes to the redirect list from the settings. A document with no recipient
/// at all is not an error (plenty are printed or posted, not mailed), so it is logged and skipped.
/// </summary>
public sealed class MailDocumentTarget(MailOptions mail, MailSender? mailer) : IDocumentTarget
{
    public string Kind => TargetKinds.Mail;

    // A second send is a second mail; nothing at the recipient is replaced. Mail runs last, so a
    // retry after a failed target has not mailed yet.
    public bool ReplacesOnRepeat => true;

    public string? Unavailable(TargetOptions target)
        => mail.Enabled && mailer is null ? "Mail is enabled but its connection is unavailable." : null;

    public DeliveryResult Deliver(DeliveryContext ctx)
    {
        var (type, data, documentId, tag, log) = (ctx.Type, ctx.Data, ctx.DocumentId, ctx.Tag, ctx.Log);
        if (mailer is null)
        {
            log.LogInformation("[{Tag}] {Id}: mail skipped: document mail is not enabled.", tag, documentId);
            return new DeliveryResult(Summary: "Not mailed: document mail is not switched on in Settings.");
        }

        var plan = mailer.PlanRecipients(data.Environment, data.RecipientEmail, data.RecipientEmailCc);
        string partyLabel = type.Party == DocumentParty.Vendor ? "vendor" : "customer";

        if (plan.Redirected && string.IsNullOrWhiteSpace(data.Environment))
            log.LogWarning(
                "[{Tag}] {Id}: the JSON carries no 'environment' — treating it as NOT production and redirecting the mail. " +
                "Update the AX export (BDCEnvironmentHelper) so production documents reach the {Party}.", tag, documentId, partyLabel);

        if (plan.To.Count == 0)
        {
            if (plan.Redirected)
                throw new InvalidOperationException(
                    $"Mail is enabled and this document came from a '{plan.Environment}' system, but no redirect " +
                    "recipients are configured. Add them in Settings > Connections > Document mail > Test recipients.");

            log.LogWarning("[{Tag}] {Id}: no recipient e-mail in the document — mail skipped.", tag, documentId);
            return new DeliveryResult(Summary: "Not mailed: the document names no recipient.");
        }

        if (string.IsNullOrWhiteSpace(data.EmailBody))
            log.LogWarning("[{Tag}] {Id}: the JSON has no emailBody — sending with an empty body.", tag, documentId);

        string subject = MailSender.BuildSubject(data.EmailSubject, type.EffectiveDisplayName, ctx.DocumentNumber, plan);
        var body = MailSender.BuildBody(data.EmailBody, type.EffectiveDisplayName, ctx.DocumentNumber, partyLabel, plan);

        try
        {
            mailer.Send(plan, subject, body, ctx.Output);
        }
        catch (Exception ex)
        {
            // Production and upload already succeeded, so name the document and the step
            // rather than letting the bare Graph error stand on its own.
            throw new InvalidOperationException(
                $"The {type.EffectiveDisplayName} was produced and uploaded, but mailing it failed: {ex.Message} " +
                "Fix the mail settings, then drop the pair back into the input folder to retry.", ex);
        }

        if (plan.Redirected)
            log.LogInformation(
                "[mail] {Id} ({Type}) REDIRECTED — environment={Env}, sent to {To} instead of {Original}; {Kind} body, attachment {File}",
                documentId, type.Id, plan.Environment, DocumentProcessor.MaskAddresses(plan.To),
                plan.OriginalTo.Count > 0 ? DocumentProcessor.MaskAddresses(plan.OriginalTo) : "(none)", body.IsHtml ? "HTML" : "text", ctx.Output.Name);
        else
            log.LogInformation(
                "[mail] {Id} ({Type}) -> {To}{Cc}; from {From}, {Kind} body, attachment {File}",
                documentId, type.Id, DocumentProcessor.MaskAddresses(plan.To),
                plan.Cc.Count > 0 ? " cc " + DocumentProcessor.MaskAddresses(plan.Cc) : "",
                mailer.From, body.IsHtml ? "HTML" : "text", ctx.Output.Name);
        string to = DocumentProcessor.MaskAddresses(plan.To) + (plan.Cc.Count > 0 ? ", copy to " + DocumentProcessor.MaskAddresses(plan.Cc) : "");
        return new DeliveryResult(Summary: plan.Redirected
            ? $"Sent to the test recipients {to} in place of the {(type.Party == DocumentParty.Vendor ? "vendor" : "customer")}: the document came from a '{plan.Environment}' system."
            : $"Sent to {to}.");
    }
}