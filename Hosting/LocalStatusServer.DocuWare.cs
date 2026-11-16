using System.Net;
using System.Text.Json;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>
/// The dashboard's DocuWare and history calls: test a connection (and list its file cabinets), read a cabinet's
/// fields for "Import fields", the documents that need attention, and putting a failed document back for a retry.
/// </summary>
public sealed partial class LocalStatusServer
{
    private sealed class DocuWareRequestDto
    {
        public string? Id { get; set; }
        public string? Url { get; set; }
        public string? UserName { get; set; }
        public string? Secret { get; set; }
        public string? CabinetId { get; set; }
    }

    private sealed class RetryRequestDto
    {
        public string? DocumentId { get; set; }
    }

    /// <summary>The connection as the dashboard shows it now; a blank password means the stored one of the same id.</summary>
    private DocuWareClient? DocuWareClientFor(DocuWareRequestDto dto, out string problem)
    {
        problem = "";
        string password = dto.Secret ?? "";
        if (password.Length == 0)
        {
            var stored = _optsMonitor.CurrentValue.Connection(dto.Id);
            password = stored is null || stored.Secret.Length == 0 ? "" : SecretProtector.Unprotect(stored.Secret);
        }
        var check = new ConnectionOptions
        {
            Id = string.IsNullOrWhiteSpace(dto.Id) ? ConnectionKinds.DocuWare : dto.Id,
            Kind = ConnectionKinds.DocuWare,
            Settings = { ["Url"] = dto.Url ?? "", ["UserName"] = dto.UserName ?? "" },
            Secret = password,
        };
        check.Normalize();
        var problems = check.Problems();
        if (problems.Count > 0) { problem = string.Join(" ", problems); return null; }
        return new DocuWareClient(check.Setting("Url"), check.Setting("UserName"), password);
    }

    private async Task PostTestDocuWare(HttpListenerContext ctx, HttpListenerRequest req)
    {
        DocuWareRequestDto? dto;
        try { dto = JsonSerializer.Deserialize<DocuWareRequestDto>(ReadBody(req), JsonOpts); }
        catch (Exception ex) { Respond(ctx, 400, new { ok = false, message = "Invalid JSON: " + ex.Message }); return; }
        var client = DocuWareClientFor(dto ?? new(), out var problem);
        if (client is null) { Respond(ctx, 200, new { ok = false, message = problem }); return; }
        try
        {
            var cabinets = await client.CabinetsAsync();
            string names = string.Join(", ", cabinets.Take(5).Select(c => c.Name)) + (cabinets.Count > 5 ? ", …" : "");
            Respond(ctx, 200, new
            {
                ok = true,
                message = cabinets.Count == 0
                    ? "Signed in, but this user sees no file cabinet. Ask the DocuWare administrator for access."
                    : $"Signed in. {cabinets.Count} file cabinet(s): {names}.",
                cabinets = cabinets.Select(c => new { id = c.Id, name = c.Name }).ToArray(),
            });
        }
        catch (DocuWareException ex) { Respond(ctx, 200, new { ok = false, message = ex.Message }); }
    }

    private async Task PostDocuWareFields(HttpListenerContext ctx, HttpListenerRequest req)
    {
        DocuWareRequestDto? dto;
        try { dto = JsonSerializer.Deserialize<DocuWareRequestDto>(ReadBody(req), JsonOpts); }
        catch (Exception ex) { Respond(ctx, 400, new { ok = false, message = "Invalid JSON: " + ex.Message }); return; }
        if (string.IsNullOrWhiteSpace(dto?.CabinetId)) { Respond(ctx, 200, new { ok = false, message = "Choose a file cabinet first." }); return; }
        var client = DocuWareClientFor(dto, out var problem);
        if (client is null) { Respond(ctx, 200, new { ok = false, message = problem }); return; }
        try
        {
            var cabinet = await client.CabinetAsync(dto.CabinetId);
            Respond(ctx, 200, new
            {
                ok = true,
                message = $"{cabinet.Fields.Count} field(s) in file cabinet \"{cabinet.Name}\".",
                cabinet = cabinet.Name,
                fields = cabinet.Fields.Select(f => new
                {
                    name = f.Name,
                    displayName = f.DisplayName,
                    type = f.Type,
                    required = f.Required,
                    length = f.Length,
                    columns = f.Columns.Select(c => new { name = c.Name, displayName = c.DisplayName, type = c.Type }).ToArray(),
                }).ToArray(),
            });
        }
        catch (DocuWareException ex) { Respond(ctx, 200, new { ok = false, message = ex.Message }); }
    }

    /// <summary>GET /history/attention: documents whose last step failed or is waiting, newest first.</summary>
    private object Attention(HttpListenerRequest req)
    {
        int limit = int.TryParse(req.QueryString["limit"], out var n) ? Math.Clamp(n, 1, 1000) : 200;
        return new
        {
            events = _ledger.Attention(limit).Select(e => new
            {
                id = e.Id,
                at = e.At,
                documentId = e.DocumentId,
                documentNumber = e.DocumentNumber,
                documentType = e.DocumentType,
                dataAreaId = e.DataAreaId,
                input = e.Input,
                step = e.Step,
                target = e.Target,
                state = e.State,
                message = e.Message,
            }).ToArray(),
        };
    }

    private void PostRetry(HttpListenerContext ctx, HttpListenerRequest req)
    {
        RetryRequestDto? dto;
        try { dto = JsonSerializer.Deserialize<RetryRequestDto>(ReadBody(req), JsonOpts); }
        catch (Exception ex) { Respond(ctx, 400, new { ok = false, message = "Invalid JSON: " + ex.Message }); return; }
        var options = _optsMonitor.CurrentValue;
        options.Normalize();
        var moved = DocumentProcessor.PutBackForRetry(options, dto?.DocumentId ?? "");
        Respond(ctx, 200, new
        {
            ok = moved.Count > 0,
            message = moved.Count > 0
                ? $"Put {moved.Count} file(s) back into the input folder. The document is processed on the next scan."
                : "No files of this document are in an error folder. It may have been retried already, or its files were removed.",
            files = moved.ToArray(),
        });
    }
}