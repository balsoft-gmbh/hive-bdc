using System.Text.Json.Serialization;

namespace Balsoft.Hive.BDC.Configuration;

/// <summary>Who a document is addressed to: an AX customer (Debitor) or a vendor (Kreditor).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocumentParty
{
    Customer,
    Vendor,
}

/// <summary>
/// How the output file is produced from the dropped JSON + PDF pair.
///   EInvoice: the JSON is mapped to EN 16931 and written as XRechnung XML or embedded
///             into the PDF as ZUGFeRD (the original Hive eInvoice feature).
///   Pdf:      the PDF the ERP rendered is delivered as-is; the JSON only carries the
///             envelope (recipient, mail subject/body, SharePoint column values).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocumentHandlerKind
{
    EInvoice,
    Pdf,
}

/// <summary>
/// One document type the service dispatches, bound from "HiveBDC:DocumentTypes[i]".
/// The <see cref="Id"/> is what the ERP writes into the JSON as <c>documentType</c>
/// (case-insensitive) and doubles as the output sub-folder, the ledger value and the
/// dashboard label. WHERE a type flows is configured per AX company right here, in
/// <see cref="Companies"/>: each entry is one dataAreaId with its own input folder, output
/// folder and SharePoint target. A type with no company entry is never picked up.
///
/// Six types ship built in (<see cref="DocumentTypeCatalog.BuiltIn"/>); any other id the ERP
/// sends is registered on first sight (PDF pass-through, routed like the folder it was found
/// in), so a new document flows with zero configuration and can be tuned afterwards.
/// </summary>
public sealed class DocumentTypeOptions
{
    /// <summary>Identifier, e.g. "SalesInvoice". Letters/digits only; matched case-insensitively.</summary>
    public string Id { get; set; } = "";

    /// <summary>Human label for the dashboard, the log and the default mail subject.</summary>
    public string DisplayName { get; set; } = "";

    public DocumentParty Party { get; set; } = DocumentParty.Customer;

    public DocumentHandlerKind Handler { get; set; } = DocumentHandlerKind.Pdf;

    /// <summary>A disabled type is not processed: its pairs are routed to error\ with a clear reason.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether documents of this type are mailed (when mail is configured at all), for every company
    /// entry that does not set its own <see cref="DocumentRoute.Mail"/>.</summary>
    public bool Mail { get; set; } = true;

    /// <summary>Whether the PDFs of this type are made smaller before delivery (<c>PdfCompressor</c>), for every company
    /// entry that does not set its own <see cref="DocumentRoute.CompressPdf"/>. Off by default.</summary>
    public bool CompressPdf { get; set; }

    /// <summary>The AX companies this type is picked up for, each with its own folders and SharePoint target.</summary>
    public List<DocumentRoute> Companies { get; set; } = new();

    /// <summary>
    /// True when this entry was created by the service itself because the ERP sent an
    /// unknown documentType. Persisted, so the dashboard can show which types still run
    /// on defaults; cleared when the entry is saved from Settings.
    /// </summary>
    public bool Discovered { get; set; }

    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;

    public bool Matches(string? id) => DocumentTypeCatalog.SameId(Id, id);

    /// <summary>Trim and default; drop company entries without an input folder. Idempotent.</summary>
    public void Normalize()
    {
        Id = DocumentTypeCatalog.CleanId(Id);
        DisplayName = DisplayName?.Trim() ?? "";
        Companies ??= new();
        foreach (var r in Companies) r?.Normalize();
        Companies.RemoveAll(r => r is null || string.IsNullOrWhiteSpace(r.Input));
    }
}

/// <summary>
/// One AX company's routing for one document type ("DocumentTypes[i]:Companies[j]"):
/// the folder the ERP drops the pairs into, where the outputs go, and the SharePoint
/// target. The pair's dataAreaId (from the JSON) selects the entry when a folder serves
/// several companies; a folder that serves one company needs no dataAreaId at all.
/// </summary>
public sealed class DocumentRoute
{
    /// <summary>AX dataAreaId, e.g. "002". Shown as the company name in the dashboard.</summary>
    public string DataAreaId { get; set; } = "";

    /// <summary>Folder scanned for {documentId}_xxx.json + matching .pdf pairs.</summary>
    public string Input { get; set; } = "";

    /// <summary>What the input folder receives: <see cref="InputFormats.Json"/> (ERP export pairs, the default) or
    /// <see cref="InputFormats.Word"/> (filled Word invoices, read and rendered by Hive BDC).</summary>
    public string InputFormat { get; set; } = InputFormats.Json;

    public bool IsWord => InputFormats.IsWord(InputFormat);

    /// <summary>Output folder. Blank = &lt;Input&gt;\out\{documentType}.</summary>
    public string Output { get; set; } = "";

    /// <summary>SharePoint target (site/library/folder), column templates and the Enabled toggle.</summary>
    public SharePointTargetOptions SharePoint { get; set; } = new();

    /// <summary>Further targets next to the SharePoint block, of any kind the installed version knows.</summary>
    public List<TargetOptions> Targets { get; set; } = new();

    /// <summary>Whether this company of the flow runs. Null (files written before 1.5.0) = on.</summary>
    public bool? Enabled { get; set; }

    public bool IsOn => Enabled ?? true;

    /// <summary>Whether this company's documents are mailed. Null (files written before 1.5.0) = as the type says.</summary>
    public bool? Mail { get; set; }

    public bool MailsFor(DocumentTypeOptions type) => Mail ?? type.Mail;

    /// <summary>Addresses that get every mail of this company as a copy (Cc), from the Live ERP and from test systems.
    /// Empty (also for files written before 1.13.0) = no copies.</summary>
    public List<string> MailCc { get; set; } = new();

    /// <summary>Addresses that get every mail of this company as a blind copy (Bcc), from the Live ERP and from test systems.
    /// Empty (also for files written before 1.14.0) = no blind copies.</summary>
    public List<string> MailBcc { get; set; } = new();

    /// <summary>Whether this company's PDFs are compressed before delivery. Null (files written before 1.12.0) = as the type says.</summary>
    public bool? CompressPdf { get; set; }

    public bool CompressesFor(DocumentTypeOptions type) => CompressPdf ?? type.CompressPdf;

    /// <summary>
    /// Where the documents come from when not the input folder itself: Kind "blob" downloads them from Azure Blob
    /// Storage into the input folder (Settings Container, Prefix, ProcessedPrefix, ErrorPrefix). Null = the folder.
    /// </summary>
    public SourceOptions? Source { get; set; }

    /// <summary>What this entry delivers to, in order: the SharePoint block when it is on, then every enabled further target.</summary>
    public IEnumerable<TargetOptions> EffectiveTargets()
    {
        if (SharePoint.Enabled) yield return TargetOptions.FromSharePoint(SharePoint);
        foreach (var t in Targets)
            if (t.Enabled) yield return t;
    }

    public bool DeliversTo(string kind) => EffectiveTargets().Any(t => t.Kind == TargetKinds.Clean(kind));

    public string OutputDirFor(DocumentTypeOptions type)
        => string.IsNullOrWhiteSpace(Output) ? Path.Combine(Input, "out", type.Id) : Output;

    public void Normalize()
    {
        DataAreaId = DataAreaId?.Trim() ?? "";
        Input = Input?.Trim() ?? "";
        Output = Output?.Trim() ?? "";
        InputFormat = InputFormats.IsWord(InputFormat) ? InputFormats.Word : InputFormats.Json;
        SharePoint ??= new SharePointTargetOptions();
        SharePoint.Normalize();
        MailCc = MailAddresses.Clean(MailCc).ToList();
        MailBcc = MailAddresses.Clean(MailBcc).ToList();
        Targets ??= new();
        Targets.RemoveAll(t => t is null);
        foreach (var t in Targets) t.Normalize();
        Targets.RemoveAll(t => t.Kind.Length == 0);
        Source?.Normalize();
        if (Source is { Kind.Length: 0 }) Source = null;
    }

    /// <summary>A copy for another document type (same company, same folders, same target).</summary>
    public DocumentRoute Clone() => new()
    {
        DataAreaId = DataAreaId,
        Input = Input,
        InputFormat = InputFormat,
        Output = "",   // the output default is per type (…\out\{type}); a copied explicit output would mix types
        SharePoint = new SharePointTargetOptions
        {
            Enabled = SharePoint.Enabled,
            SiteUrl = SharePoint.SiteUrl,
            Library = SharePoint.Library,
            Folder = SharePoint.Folder,
            Fields = new Dictionary<string, string>(SharePoint.Fields),
        },
        Targets = Targets.Select(t => t.Clone()).ToList(),
        Source = Source is null ? null : new SourceOptions
        {
            Kind = Source.Kind,
            Connection = Source.Connection,
            Settings = new Dictionary<string, string>(Source.Settings, StringComparer.OrdinalIgnoreCase),
        },
    };
}

/// <summary>The document types that ship with the product, in AX terminology.</summary>
public static class DocumentTypeCatalog
{
    /// <summary>The type an old-style export (no documentType in the JSON) is treated as.</summary>
    public const string DefaultId = "SalesInvoice";

    public static IReadOnlyList<DocumentTypeOptions> BuiltIn => new[]
    {
        new DocumentTypeOptions { Id = "SalesInvoice",      DisplayName = "Sales invoice",       Party = DocumentParty.Customer, Handler = DocumentHandlerKind.EInvoice },
        new DocumentTypeOptions { Id = "SalesConfirmation", DisplayName = "Order confirmation",  Party = DocumentParty.Customer, Handler = DocumentHandlerKind.Pdf },
        new DocumentTypeOptions { Id = "SalesQuotation",    DisplayName = "Quotation",           Party = DocumentParty.Customer, Handler = DocumentHandlerKind.Pdf },
        new DocumentTypeOptions { Id = "CollectionLetter",  DisplayName = "Collection letter",   Party = DocumentParty.Customer, Handler = DocumentHandlerKind.Pdf },
        new DocumentTypeOptions { Id = "PurchaseOrder",     DisplayName = "Purchase order",      Party = DocumentParty.Vendor,   Handler = DocumentHandlerKind.Pdf },
        new DocumentTypeOptions { Id = "PaymentAdvice",     DisplayName = "Payment advice",      Party = DocumentParty.Vendor,   Handler = DocumentHandlerKind.Pdf },
    };

    /// <summary>
    /// The entry a never-seen documentType gets: PDF pass-through, mail on, routed like the
    /// company entry it was found under (<paramref name="seed"/>), so it keeps flowing from
    /// that folder to that SharePoint target until someone changes it.
    /// </summary>
    public static DocumentTypeOptions Discovered(string id, DocumentParty party, DocumentRoute? seed)
    {
        var t = new DocumentTypeOptions
        {
            Id = CleanId(id),
            DisplayName = SplitPascalCase(CleanId(id)),
            Party = party,
            Handler = DocumentHandlerKind.Pdf,
            Discovered = true,
        };
        if (seed is not null) t.Companies.Add(seed.Clone());
        return t;
    }

    public static bool SameId(string? a, string? b)
        => string.Equals(CleanId(a), CleanId(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Trim and strip anything that is not a letter, digit, '-' or '_' so the id is safe as a folder name.</summary>
    public static string CleanId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var chars = id.Trim().Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray();
        return new string(chars);
    }

    public static bool IsValidId(string? id) => CleanId(id).Length > 0 && CleanId(id) == id?.Trim();

    /// <summary>"SalesConfirmation" -> "Sales confirmation", for a readable default label.</summary>
    private static string SplitPascalCase(string id)
    {
        if (id.Length == 0) return id;
        var sb = new System.Text.StringBuilder(id.Length + 4);
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(id[i - 1]))
            {
                sb.Append(' ');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>
/// One folder the service scans, with every (type, company entry) that points at it. A
/// folder used by exactly one document type implies that type for a JSON that carries no
/// documentType. A folder that only disabled types point at is not scanned at all: the
/// installer default (C:\Temp\HiveBDC) stays on every type a customer never configured,
/// and polling it only produced access-denied errors every cycle (customer install, 2026-10-11).
/// A folder shared with an enabled type is still scanned, so a disabled type's pairs
/// dropped there go to error\ with that reason.
/// </summary>
public sealed record ScanFolder(string Path, IReadOnlyList<(DocumentTypeOptions Type, DocumentRoute Route)> Routes)
{
    public string ProcessedDir => System.IO.Path.Combine(Path, "processed");
    public string WorkDir => System.IO.Path.Combine(Path, "work");

    /// <summary>True when a company entry using this folder takes Word documents.</summary>
    public bool AcceptsWord => Routes.Any(r => r.Route.IsWord);
    public string ErrorDir => System.IO.Path.Combine(Path, "error");

    public string? ImpliedTypeId
    {
        get
        {
            var ids = Routes.Select(r => r.Type.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return ids.Count == 1 ? ids[0] : null;
        }
    }

    /// <summary>Build the distinct scan folders; a folder used only by switched-off types or companies is left out.</summary>
    public static IReadOnlyList<ScanFolder> Build(IEnumerable<DocumentTypeOptions> types)
    {
        static string Key(string p) => System.IO.Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant();
        var byFolder = new Dictionary<string, (string Path, List<(DocumentTypeOptions, DocumentRoute)> Routes)>();
        foreach (var t in types)
            foreach (var r in t.Companies)
            {
                if (string.IsNullOrWhiteSpace(r.Input)) continue;
                string k;
                try { k = Key(r.Input); } catch { continue; }   // an unusable path is reported at startup, not here
                if (!byFolder.TryGetValue(k, out var e)) byFolder[k] = e = (r.Input, new());
                e.Routes.Add((t, r));
            }
        return byFolder.Values
            .Where(e => e.Routes.Any(r => r.Item1.Enabled && r.Item2.IsOn))
            .Select(e => new ScanFolder(e.Path, e.Routes))
            .ToList();
    }
}
