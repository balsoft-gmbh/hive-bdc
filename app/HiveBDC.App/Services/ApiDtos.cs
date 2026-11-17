using System.Text.Json.Serialization;

namespace Balsoft.Hive.BDC.App.Services;

// Small, app-owned response DTOs for the loopback status API. Kept deliberately
// separate from the service's own types: the app only ever deserializes the JSON
// the loopback API emits — it never references the service assembly, never reads
// document content, and never touches the SQLite ledger directly. Deserialization uses PropertyNameCaseInsensitive so the
// camelCase JSON binds cleanly onto these PascalCase records.

/// <summary>Contents of %ProgramData%\HiveBDC\app-endpoint.json.</summary>
public sealed record EndpointFile(
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("token")] string Token);

/// <summary>GET /status. Multi-company: reports how many companies are configured,
/// whether ANY of them has SharePoint upload enabled, and the document types in play.</summary>
public sealed record StatusDto(
    bool Running,
    string? Version,
    int CompanyCount,
    int PollSeconds,
    bool SharePoint,
    LicenseDto? License,
    int DocumentTypeCount = 0,
    int FolderCount = 0,
    List<DocumentTypeStatusDto>? DocumentTypes = null);

/// <summary>
/// One document type as the service currently sees it (GET /status). Licensed is false when
/// the installed license lacks the feature this type's handler needs.
/// </summary>
public sealed record DocumentTypeStatusDto(
    string? Id,
    string? DisplayName,
    string? Party,
    string? Handler,
    bool Enabled,
    bool Mail,
    bool SharePoint,
    int CompanyCount,
    bool Discovered,
    bool Licensed);

public sealed record LicenseDto(
    string? State,
    string? Message,
    string? Customer,
    string? Expires,
    bool Blocked = false,
    List<string>? Features = null);

/// <summary>GET /metrics. Month may be absent/null on some builds.</summary>
public sealed record MetricsDto(
    int Today,
    int? Month,
    long Total,
    List<RecentExportDto>? Recent,
    List<TypeCountDto>? MonthByType = null);

public sealed record TypeCountDto(string? DocumentType, int Count);

public sealed record RecentExportDto(
    string? DocumentId,
    string? DocumentType,
    string? Format,
    string? OutputFile,
    string? TargetSite,
    long SizeBytes,
    string? ExportedAt,
    string? DataAreaId = null,
    string? Source = null);

/// <summary>GET /logs?tail=N.</summary>
public sealed record LogsDto(
    string? File,
    List<string>? Lines,
    string? Note,
    string? Error);

// ── Config API (GET/POST /config, POST /config/test-sharepoint) ─────────────
// One mutable DTO set serves both directions of /config. On GET, the service fills
// every field and reports the SHARED secret only as SecretSet (the raw secret is
// NEVER returned). On POST, the app sends the same shape and MAY include
// SharePointAuth.ClientSecret: a null/empty ClientSecret means "keep the stored one".
// Nulls are omitted on write (see LoopbackClient.JsonWriteOpts) so blanks map to "keep".

/// <summary>Whole settings payload for GET/POST /config.</summary>
public sealed class ConfigDto
{
    [JsonPropertyName("pollSeconds")] public int PollSeconds { get; set; }
    [JsonPropertyName("logDir")] public string? LogDir { get; set; }
    [JsonPropertyName("license")] public LicenseConfigDto? License { get; set; }
    /// <summary>SHARED SharePoint auth (tenant/client + secretSet/clientSecret).</summary>
    [JsonPropertyName("sharePointAuth")] public SharePointAuthConfigDto? SharePointAuth { get; set; }
    /// <summary>SHARED document-mail config (own app registration, sender mailbox, redirect list).</summary>
    [JsonPropertyName("mail")] public MailConfigDto? Mail { get; set; }
    /// <summary>The document types the service dispatches, each with its company entries. Absent on POST => the service keeps its stored list.</summary>
    [JsonPropertyName("documentTypes")] public List<DocumentTypeConfigDto>? DocumentTypes { get; set; }
    [JsonPropertyName("connections")] public List<ConnectionConfigDto>? Connections { get; set; }
    /// <summary>Import flows as the service stores them; kept as JSON so unknown properties survive a save.</summary>
    [JsonPropertyName("flows")] public System.Text.Json.Nodes.JsonArray? Flows { get; set; }
}

/// <summary>A named sign-in (DocuWare, ...). GET reports only HasSecret; POST sends Secret, blank = keep the stored one.</summary>
public sealed class ConnectionConfigDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("settings")] public Dictionary<string, string>? Settings { get; set; }
    [JsonPropertyName("hasSecret")] public bool? HasSecret { get; set; }
    [JsonPropertyName("secret")] public string? Secret { get; set; }
}

/// <summary>One further target of a company entry. Kinds this dashboard does not edit are sent back unchanged.</summary>
public sealed class TargetConfigDto
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("connection")] public string? Connection { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("settings")] public Dictionary<string, string>? Settings { get; set; }
    [JsonPropertyName("fields")] public Dictionary<string, string>? Fields { get; set; }
}

/// <summary>One document type (GET/POST /config). Id is what the ERP writes as documentType.</summary>
public sealed class DocumentTypeConfigDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    /// <summary>"Customer" or "Vendor".</summary>
    [JsonPropertyName("party")] public string? Party { get; set; }
    /// <summary>"EInvoice" (XRechnung/ZUGFeRD from the JSON) or "Pdf" (deliver the ERP's PDF).</summary>
    [JsonPropertyName("handler")] public string? Handler { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("mail")] public bool Mail { get; set; } = true;
    /// <summary>GET only: the service registered this type itself when the ERP first sent it.</summary>
    [JsonPropertyName("discovered")] public bool Discovered { get; set; }
    /// <summary>The AX companies this type is picked up for, each with folders and a SharePoint target.</summary>
    [JsonPropertyName("companies")] public List<RouteConfigDto>? Companies { get; set; }
}

/// <summary>One company entry of a document type: dataAreaId, folders, SharePoint target.</summary>
public sealed class RouteConfigDto
{
    [JsonPropertyName("dataAreaId")] public string? DataAreaId { get; set; }
    [JsonPropertyName("input")] public string? Input { get; set; }
    /// <summary>"Json" (ERP export pairs) or "Word" (filled Word invoices).</summary>
    [JsonPropertyName("inputFormat")] public string? InputFormat { get; set; }
    [JsonPropertyName("output")] public string? Output { get; set; }
    /// <summary>Whether this company of the flow runs. A service before 1.5.0 sends none (on).</summary>
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
    /// <summary>Whether this company's documents are mailed. GET sends the effective value; a service before 1.5.0 sends none.</summary>
    [JsonPropertyName("mail")] public bool? Mail { get; set; }
    [JsonPropertyName("sharePoint")] public CompanySharePointConfigDto? SharePoint { get; set; }
    [JsonPropertyName("targets")] public List<TargetConfigDto>? Targets { get; set; }
}

/// <summary>SHARED document mail. SecretSet is read-only (GET); ClientSecret is write-only (POST).</summary>
public sealed class MailConfigDto
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    /// <summary>Blank => the SharePoint tenant is used.</summary>
    [JsonPropertyName("tenantId")] public string? TenantId { get; set; }
    [JsonPropertyName("clientId")] public string? ClientId { get; set; }
    /// <summary>GET only: true when a secret is stored. Left null on POST so it isn't sent.</summary>
    [JsonPropertyName("secretSet")] public bool? SecretSet { get; set; }
    /// <summary>POST only: a new plaintext secret. Null/empty => keep the stored one.</summary>
    [JsonPropertyName("clientSecret")] public string? ClientSecret { get; set; }
    /// <summary>Mailbox documents are sent as.</summary>
    [JsonPropertyName("from")] public string? From { get; set; }
    /// <summary>Where documents from a non-production AX are sent instead of to the customer or vendor.</summary>
    [JsonPropertyName("redirectTo")] public List<string>? RedirectTo { get; set; }
    [JsonPropertyName("saveToSentItems")] public bool SaveToSentItems { get; set; } = true;
}

/// <summary>SHARED SharePoint auth. SecretSet is read-only (GET); ClientSecret is write-only (POST).</summary>
public sealed class SharePointAuthConfigDto
{
    [JsonPropertyName("tenantId")] public string? TenantId { get; set; }
    [JsonPropertyName("clientId")] public string? ClientId { get; set; }
    /// <summary>GET only: true when a secret is stored. Left null on POST so it isn't sent.</summary>
    [JsonPropertyName("secretSet")] public bool? SecretSet { get; set; }
    /// <summary>POST only: a new plaintext secret. Null/empty => keep the stored one.</summary>
    [JsonPropertyName("clientSecret")] public string? ClientSecret { get; set; }
}

/// <summary>A SharePoint TARGET (site/library/folder), Enabled toggle, and column Fields, used per company entry
/// and by the test call. Auth is NOT here — it is shared (<see cref="SharePointAuthConfigDto"/>).</summary>
public sealed class CompanySharePointConfigDto
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("siteUrl")] public string? SiteUrl { get; set; }
    [JsonPropertyName("library")] public string? Library { get; set; }
    [JsonPropertyName("folder")] public string? Folder { get; set; }
    [JsonPropertyName("fields")] public Dictionary<string, string>? Fields { get; set; }
}

public sealed class LicenseConfigDto
{
    [JsonPropertyName("file")] public string? File { get; set; }
}

/// <summary>POST /config/test-sharepoint body: one company entry's target + an optional secret.
/// A blank/absent ClientSecret => the service tests with the stored shared secret.</summary>
public sealed class TestSharePointRequestDto
{
    [JsonPropertyName("sharePoint")] public CompanySharePointConfigDto? SharePoint { get; set; }
    [JsonPropertyName("clientSecret")] public string? ClientSecret { get; set; }
}

/// <summary>POST /config response.</summary>
public sealed record SaveConfigResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("errors")] List<string>? Errors);

/// <summary>POST /config/test-sharepoint response.</summary>
public sealed record TestSharePointResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string? Message);

/// <summary>POST /config/test-mail body: the mail settings to test. A blank ClientSecret uses the stored one.</summary>
public sealed class TestMailRequestDto
{
    [JsonPropertyName("mail")] public MailConfigDto? Mail { get; set; }
    /// <summary>Tenant to test against; blank => the stored mail tenant, else the SharePoint tenant.</summary>
    [JsonPropertyName("tenantId")] public string? TenantId { get; set; }
}

/// <summary>POST /config/test-mail response.</summary>
public sealed record TestMailResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string? Message);

/// <summary>POST /license body: the raw .lic file text (+ its file name, for the service log).</summary>
public sealed class LicenseUploadRequestDto
{
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("fileName")] public string? FileName { get; set; }
}

/// <summary>POST /license response. Ok=false carries the rejection reason (bad signature,
/// wrong tenant, expired). On success State/Customer/Expires describe the installed license.</summary>
public sealed record LicenseUploadResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("customer")] string? Customer,
    [property: JsonPropertyName("expires")] string? Expires);


// ── DocuWare and document history ───────────────────────────────────────────

public sealed class DocuWareRequestDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("userName")] public string? UserName { get; set; }
    [JsonPropertyName("secret")] public string? Secret { get; set; }
    [JsonPropertyName("cabinetId")] public string? CabinetId { get; set; }
}

public sealed record DocuWareCabinetDto(string? Id, string? Name);

// ── inbound sources (GET /sources, POST /sources/choices, POST /sources/sample) ──────────────

public sealed class SourcesDto
{
    [JsonPropertyName("sources")] public List<SourceInfoDto>? Sources { get; set; }
}

/// <summary>A document system this version of the service reads from, and the settings an inbound flow from it needs.</summary>
public sealed class SourceInfoDto
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("connectionKind")] public string? ConnectionKind { get; set; }
    [JsonPropertyName("connected")] public bool Connected { get; set; }
    [JsonPropertyName("recordNoun")] public string? RecordNoun { get; set; }
    [JsonPropertyName("defaultFileName")] public string? DefaultFileName { get; set; }
    [JsonPropertyName("fieldsLookup")] public string? FieldsLookup { get; set; }
    [JsonPropertyName("settings")] public List<SourceSettingDto>? Settings { get; set; }

    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
}

public sealed class SourceSettingDto
{
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("step")] public string? Step { get; set; }
    [JsonPropertyName("required")] public bool Required { get; set; }
    [JsonPropertyName("lookup")] public string? Lookup { get; set; }
    [JsonPropertyName("dependsOn")] public string? DependsOn { get; set; }
    [JsonPropertyName("hint")] public string? Hint { get; set; }
}

public sealed class SharePointDraftDto
{
    [JsonPropertyName("tenantId")] public string? TenantId { get; set; }
    [JsonPropertyName("clientId")] public string? ClientId { get; set; }
    [JsonPropertyName("secret")] public string? Secret { get; set; }
}

/// <summary>Which source, which list, its settings so far, and the unsaved connection values of the form.</summary>
public sealed class SourceRequestDto
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("lookup")] public string? Lookup { get; set; }
    [JsonPropertyName("connection")] public string? Connection { get; set; }
    [JsonPropertyName("settings")] public Dictionary<string, string>? Settings { get; set; }
    [JsonPropertyName("docuWare")] public DocuWareRequestDto? DocuWare { get; set; }
    [JsonPropertyName("sharePoint")] public SharePointDraftDto? SharePoint { get; set; }
}

public sealed record SourceChoiceDto(string? Value, string? Label, string? Detail)
{
    public override string ToString() => Label ?? Value ?? "";
}

public sealed class SourceChoicesResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("choices")] public List<SourceChoiceDto>? Choices { get; set; }
}

public sealed record SourceValueDto(string? Name, string? Value);

public sealed class SourceSampleResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("values")] public List<SourceValueDto>? Values { get; set; }
}

public sealed record TestDocuWareResult(bool Ok, string? Message, List<DocuWareCabinetDto>? Cabinets);

public sealed record DocuWareColumnDto(string? Name, string? DisplayName, string? Type);

public sealed record DocuWareFieldDto(string? Name, string? DisplayName, string? Type, bool Required, int Length, List<DocuWareColumnDto>? Columns);

public sealed record DocuWareFieldsResult(bool Ok, string? Message, string? Cabinet, List<DocuWareFieldDto>? Fields);

public sealed record HistoryEventDto(
    long Id, string? At, string? DocumentId, string? DocumentNumber, string? DocumentType, string? DataAreaId,
    string? Input, string? Step, string? Target, string? State, string? Message);

public sealed record HistoryDto(List<HistoryEventDto>? Events);

public sealed class RetryRequestDto
{
    [JsonPropertyName("documentId")] public string? DocumentId { get; set; }
}

public sealed record RetryResult(bool Ok, string? Message, List<string>? Files);