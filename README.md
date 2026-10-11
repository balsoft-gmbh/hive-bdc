# Hive BDC, by Balsoft

> **Source available, not open source.** The code is public so customers,
> auditors and partners can see exactly what runs on their servers. Using,
> copying, changing or distributing it requires a licence from Balsoft GmbH
> (see [LICENSE](LICENSE) and [EULA.md](EULA.md)). Balsoft develops and
> maintains the product; external contributions are not accepted (see
> [CONTRIBUTING.md](CONTRIBUTING.md)). Licensing: info@balsoft.de.

*Hive BDC stands for Balsoft Data Connector: it connects Microsoft Dynamics with your
customers' and vendors' mailboxes, your archive and the e-invoice formats.*

Hive BDC dispatches the documents **Dynamics AX / Dynamics 365 Finance**
produces for customers and vendors: it watches a folder per company, turns each
export into the deliverable (a German **e-invoice** as **XRechnung** or **ZUGFeRD**
for sales invoices, the ERP's own **PDF** for everything else), then **mails** it
and/or uploads it to **SharePoint**. It ships as two parts:

- **Service** (`HiveBDC.exe`) — a .NET 10 Windows service that watches one
  input folder per company, produces and delivers each document, and keeps a
  local activity log and export ledger. Runs headless under the SCM.
- **Dashboard** (`HiveBDC.App.exe`) — a WPF app to monitor the service,
  see the licence and export counts, read the log, configure companies, document
  types and mail, start/stop the service, renew the licence, and install updates.

Everything runs **100% on the customer's machine** — document content never
leaves it except to the recipients you configure. The dashboard talks to the
service only over a loopback (`127.0.0.1`) API.

## Document types

The service knows six types out of the box (AX terminology, English ids):

| `documentType` | AX document | Party | Handler |
|---|---|---|---|
| `SalesInvoice` | Rechnung | Customer | **EInvoice**: XRechnung XML or ZUGFeRD PDF/A-3 |
| `SalesConfirmation` | Auftragsbestätigung | Customer | Pdf |
| `SalesQuotation` | Angebot | Customer | Pdf |
| `CollectionLetter` | Mahnung | Customer | Pdf |
| `PurchaseOrder` | Bestellung | Vendor | Pdf |
| `PaymentAdvice` | Zahlungsavis (SEPA) | Vendor | Pdf |

Per type you set (dashboard → **Settings → Document types**, or `DocumentTypes[]`
in `appsettings.json`): the **party** ("Sent to"), the **handler** ("Produced
as"), whether it is **enabled**, whether it is **mailed**, and its
**companies**: one entry per AX company the type is picked up for, each with
its own input folder, output folder and SharePoint target (see Companies).

**Adding a type needs no code and no config.** When the ERP drops a JSON whose
`documentType` is not listed into any watched folder, the service registers it
on the spot as a PDF pass-through (party from the JSON's `party`, else
Customer; mail on) with the same company entry as the folder it arrived in, logs
a warning, and processes it. It appears in Settings marked **auto-registered**
until you save it. A type you never want processed is set to disabled; its pairs
then go to `error\` with that reason. An input folder that only disabled types
point at is not scanned at all, so the installer default on an unused type
causes no errors.

## How dispatch works

For each document the AX/D365 export drops a `{documentId}_{seq}.json` and, for
every type except an XRechnung invoice, a matching `.pdf` into a company's input
folder. The JSON is an **envelope** every type shares, plus the invoice payload
for e-invoices:

```jsonc
{
  "documentType": "PurchaseOrder",     // one of the configured ids (case-insensitive)
  "documentNumber": "PO0001234",        // the ERP's number; blank => invoiceNumber, else the file id
  "party": "Vendor",                    // Customer | Vendor (used when a new type is auto-registered)
  "dataAreaId": "002",                  // usable in SharePoint column templates
  "environment": "Live",                // Live | Test | Dev — only Live mails the real recipient
  "recipientName": "...",
  "recipientEmail": "orders@vendor.de", // ; separated list allowed
  "recipientEmailCc": "",
  "emailSubject": "Bestellung %1",      // %1 => documentNumber
  "emailBody": "...",                   // plain text
  // SalesInvoice only (the e-invoice payload):
  "eInvoiceType": "zugferd",            // xrechnung | zugferd
  "issueDate": "...", "dueDate": "...", "currency": "EUR", "sender*": "...", "vatRate": "19",
  "lineItems": [ { "description": "...", "quantity": "1", "unit": "piece", "unitPrice": "10.00",
                   "vatRate": "7", "vatCategory": "S" } ],   // per-line VAT optional; header vatRate is the default
  // document type and settlement-house identifiers (all optional)
  "invoiceTypeCode": "381",             // UNTDID 1001: 380 invoice (default), 381 credit note, 384 correction,
                                        // 83 / 84 bonus credit note / bonus invoice; a negative total without a code = 381
  "zrKennzeichen": "42",                // Aktivbank ZR-Kennzeichen -> BT-12 contract reference, zero-padded to 0042
  "senderId": "99001",                  // Vertragslieferantennummer -> BT-29 seller ID
  "senderGln": "4000001000005",         // 13 digits -> BT-29-0 seller GLN (scheme 0088)
  "recipientId": "99002",               // Anschlusshausnummer -> BT-46 buyer ID
  "recipientGln": "",                   // 13 digits -> BT-46-0 buyer GLN; anything else is ignored with a warning
  // payment terms (BT-20): the net term from dueDate (+ optional text) and the cash discount(s)
  "paymentTermsText": "Bitte zahlen Sie bis zum 22 Juli 2026 ohne Abzug.",   // optional, default generated
  "cashDiscountDate": "2026-07-02", "cashDiscountPercent": "2", "cashDiscountAmount": "77.30",  // one Skonto
  "cashDiscounts": [ { "date": "2026-07-02", "percent": "3", "amount": "115.95" },             // or a chain
                     { "date": "2026-07-12", "percent": "2", "amount": "77.30" } ]
}
```

Credit notes carry **positive** amounts with the sign in the type code (EN 16931,
and what Aktivbank requires): when the type is not 380, or the lines add up
negative, quantities and prices are written as absolute values. On a plain
invoice a negative unit price is written as a positive price with the sign moved
to the quantity (BR-27), so a discount line keeps its effect. The VAT breakdown
(BT-116 to BT-119) is emitted **per rate and category**, so an invoice mixing
19 % and 7 % lines is representable; a line without `vatRate` uses the header rate.

Payment terms land in the XML as one `SpecifiedTradePaymentTerms`: the readable
lines plus, per discount, the machine-readable XRechnung line
`#SKONTO#TAGE=10#PROZENT=2.00#` (days counted from `issueDate`, or `days` /
`cashDiscountDays` when given), and the net `DueDateDateTime`. Without the
discount fields only the net term is written, as before.

A JSON **without** `documentType` is treated as `SalesInvoice`, so an AX export that
only writes invoices keeps working. Any extra top-level field the ERP writes stays
available to the SharePoint column templates.

### Extra e-invoice fields

Any additional key in the invoice JSON that names an EN 16931 business term is
written to the XML when it has a value. Keys are matched ignoring case, spaces,
`-`, `_` and `.`, so AX can map a field to `purchase order reference`,
`PurchaseOrderReference` or `BT-13` alike.

| Term | Recognised keys (examples) |
|---|---|
| BT-10 Buyer reference | `buyerReference`, `leitwegId`, `BT-10` |
| BT-11 Project reference | `projectReference`, `projektnummer` |
| BT-12 Contract reference | `contractReference` (the Aktivbank `zrKennzeichen` wins) |
| BT-13 Purchase order reference | `purchaseOrderReference`, `purchase order reference`, `customerOrderNumber`, `bestellnummer` |
| BT-14 Sales order reference | `orderReference`, `salesOrderNumber`, `auftragsnummer` |
| BT-15 / BT-16 Receiving / despatch advice | `receivingAdviceReference`, `deliveryNoteNumber`, `packingSlipNumber` |
| BT-17 Tender or lot | `tenderReference` |
| BT-19 Buyer accounting reference | `buyerAccountingReference`, `costCenter` |
| BT-25 / BT-26 Preceding invoice | `precedingInvoiceNumber`, `precedingInvoiceDate` |
| BT-72 Delivery date | `deliveryDate`, `lieferdatum` (else the issue date) |
| BG-13 Deliver to (BT-70, 75, 77, 78, 80) | `deliveryName`, `deliveryStreet`, `deliveryCity`, `deliveryPostcode`, `deliveryCountry` (country falls back to the buyer's; BT-80 is required for intra-community supplies, BR-IC-12) |
| BT-83 / BT-85 / BT-86 Payment | `paymentReference`, `bankAccountHolder`, `bankSwift` / `bic` |
| Line BT-127, 132, 133 | `lineNote`, `orderLineReference`, `costCenter` |
| Line BT-155, 156, 157, 159 | `itemId`, `customerItemId`, `gtin` / `ean`, `originCountry` |
| Line BT-149 / BT-146 price base quantity | `priceUnit` (an AX price unit such as 100) with `priceUnitPrice` (the price per that quantity); without the latter the per-piece `unitPrice` is scaled |
| Line BT-141 / BT-144 charge | `lineChargeAmount`, `lineChargeReason` (an AX `SalesMarkup` the ERP folded into its line amount) |
| Line BT-136 / BT-139 allowance | `lineDiscountAmount`, `lineDiscountReason` |

Without `buyerReference` or a purchase order key, `reference` fills BT-10 and
BT-13 as before. The log names every term taken from such a key and lists the
JSON keys that did not go into the XML.

Handlers:

- **EInvoice** — `eInvoiceType = xrechnung` writes `{documentNumber}.xml` (EN 16931 /
  XRechnung 3.0.2, CII syntax); `eInvoiceType = zugferd` writes `{documentNumber}.pdf`
  (PDF/A-3 carrier with the same CII XML embedded, ZUGFeRD 2.5.2 / Factur-X 1.09.2, profile XRECHNUNG). The
  mapping recomputes amounts from `quantity × unitPrice` (or `quantity × priceUnitPrice /
  priceUnit`) plus line charges minus line discounts, a header or per-line `vatRate`, tax
  category `S` (or `E` at rate 0), a country-name → ISO map, and a unit-code map. A cash
  discount given as an amount only gets its percent derived from the gross total. AX 2009 PDFs that lack a closing `endobj` are repaired
  on a temp copy before embedding.
- **Pdf** — the ERP's PDF is copied to the output folder as `{documentNumber}.pdf`
  and delivered untouched.

Output files are named after the document number (`documentNumber`, else
`invoiceNumber`, else the id from the JSON file name), so documents that share
an id, such as purchase order versions `4500001234-1` and `-2`, stay separate
files locally and in SharePoint. Characters Windows or SharePoint reject become `_`.

Outputs land in the configured output folder (default `<Input>\out\{documentType}`).
Processed inputs move to `<Input>\processed\`. Bad data or a failed external
upload/mail request routes the pair to `<Input>\error\`; drop it back to retry.
Missing PDFs, incomplete writes, unresolved company routes, unavailable required
delivery clients and missing license features remain pending in the input folder.
They resume automatically after the input or configuration is repaired.

## Companies (per `dataAreaId`, inside each document type)

The service serves several AX companies at once, and the routing is defined
**per document type**: each type lists the companies it is picked up for
(**Settings → Document types → Companies**, or `DocumentTypes[i].Companies` in
`appsettings.json`). Every entry has:

- **Company** (`DataAreaId`) — the AX company. The JSON's `dataAreaId` selects
  the entry when one folder serves several companies; a folder that serves one
  company needs no `dataAreaId` in the JSON at all, the folder's company is used.
  An auto-registered type that arrives from another company's folder gets that
  company's entry added (copied from the folder); a configured type without an
  entry for that company holds the pair until the company is added.
- **Input folder** — where the ERP drops that type's `.json` + `.pdf` pairs
  (e.g. one Lasernet destination per report). Several types may share one
  folder; the JSON's `documentType` tells them apart. A folder assigned to
  exactly one type **implies that type** for a JSON without `documentType`, so
  routing by folder alone works. `processed\` and `error\` live under that
  folder.
- **Output folder** — blank = `<Input>\out\{documentType}`.
- **SharePoint** — enabled, site URL, library (the name from the library's URL,
  e.g. `SalesInvoices`, its display name, or the pasted library
  URL), optional folder and the column templates for that company and type.
  Slashes are tidied on load and save: `…/sites/X/` and `…/sites/X` are the same
  site, `/Library/`, `Library/` and `Library` the same library.

The SharePoint **app** (tenant / client / secret) and the **mail** app are
shared and live under **Connections**. A settings file with the older
company list is migrated on load: every old company becomes an entry under
every type, and the next dashboard save persists the new shape.

## Licensing

A valid **signed licence** (`.lic`, bound to the customer's Microsoft 365 tenant)
is **always required**. It is verified offline against an embedded Balsoft
certificate and carries **features**:

- `einvoice` — XRechnung / ZUGFeRD generation (the EInvoice handler)
- `documents` — PDF dispatch of every other document type (the Pdf handler)
- `sharepoint` — SharePoint upload

The licence **warns** in the dashboard as expiry approaches; after expiry there
is a **7-day grace** period; past grace (or with no/invalid licence)
**processing stops**, while the dashboard stays visible and explains why.
Documents accumulate in the input folder and resume automatically once a valid
licence is installed. A licence for an earlier Balsoft product (Hive DocFlow,
Hive eInvoice) is rejected with a clear message; Balsoft issues a Hive BDC licence.

Install or renew the licence from the dashboard (**Renew licence…**) — the `.lic`
Balsoft emails you is safe to email (it is useless on any other tenant).

## Updates

The dashboard checks a public update feed (**Help → Check for updates**),
downloads the signed `Setup.exe`, verifies its SHA-256 and Balsoft Authenticode
signature, runs it silently, and relaunches. Windows verifies the signed file digest,
certificate trust and revocation status; the publisher must be exactly `Balsoft GmbH`.
Hive BDC has its own feed (container `hive-bdc`), so a dashboard of an earlier
Balsoft product never offers Hive BDC as an update. The full
version history is in **Help → Release notes** (works offline).

## Install

Run the single signed **`HiveBDC-Setup-vX.Y.Z.exe`**. It elevates via UAC,
installs per-machine to `%ProgramFiles%\Balsoft\Hive BDC`, registers and
starts the service, installs the dashboard, and provides a clean uninstaller
("Apps & features", or `uninstall\HiveBDC-Setup.exe /UNINSTALL`). Setup is built with
Balsoft's own installer package
[Balsoft.Hive.Installer.Windows](https://www.nuget.org/packages/Balsoft.Hive.Installer.Windows). An upgrade
keeps the settings and the service account. An upgrade backs up the files it replaces and
restores them, and restarts the service, when a later step fails. Setup never
replaces a newer installed version (exit code 7; `/ALLOWDOWNGRADE` overrides it),
and the same version again is a repair. Setup writes a
log (`/LOG=path`, else `%TEMP%\Setup Log <date> #nnn.txt`); exit codes: 0 done,
1 could not start, 2 cancelled, 4 install failed, 7 preparing failed.
The installer only **registers the service** — input folders, document types,
SharePoint and mail are configured afterwards in the dashboard. (The dashboard
runs elevated so it can start/stop the service.)

Setup verifies the service account and password with Windows before it creates
the service, and will not continue while the sign-in fails.

Silent install for IT (blank account ⇒ LocalSystem):

```cmd
HiveBDC-Setup-vX.Y.Z.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART ^
  [/SvcAccount=DOMAIN\svc-hivebdc /SvcPassword=***] [/DIR="D:\Apps\Hive BDC"] ^
  [/DESKTOPICON] [/LOG="C:\Temp\hivebdc-setup.log"]
rem silent uninstall
"%ProgramFiles%\Balsoft\Hive BDC\uninstall\HiveBDC-Setup.exe" /UNINSTALL /VERYSILENT /SUPPRESSMSGBOXES
```

`/SILENT` shows only the progress window.

Alternatives: `HiveBDC.exe install` (interactive console) or
`install --quiet [--account DOMAIN\user] [--password ***]` (unattended, must
already be elevated). The service account needs *Log on as a service* and
read/write on the input/output folders (including any UNC share).

### Moving from Hive DocFlow

Hive BDC (1.0) replaces Hive DocFlow (2.x) and is installed fresh, with its own installer
identity, service (`HiveBDC`), program folder and `%ProgramData%\HiveBDC`.

1. Uninstall **Hive DocFlow** from *Apps & features*. Input folders and their contents are untouched.
2. Run the Hive BDC Setup.
3. Copy `appsettings.json` from `%ProgramFiles%\Balsoft\Hive DocFlow` to
   `%ProgramFiles%\Balsoft\Hive BDC` (its `HiveDocFlow` section is read as is and saved under
   `HiveBDC` on the next dashboard save), then restart the service.
4. Install the new license file from Balsoft; Hive DocFlow licenses are not accepted.

The export ledger and logs start fresh; the old history stays under `%ProgramData%\HiveDocFlow`
until you delete it.

## SharePoint export (optional, per company and per type)

When a company entry of a document type has SharePoint on, every produced file
of that type and company is uploaded to that entry's document library via
Microsoft Graph, and metadata columns are stamped from the document JSON. The
**target lives in the settings, not in the JSON**.

Prerequisite: an Entra ID app registration in the target tenant with the
**application** permission `Sites.ReadWrite.All` (or `Sites.Selected` granted on
the site) and a client secret. The secret is stored **DPAPI-encrypted**
(`enc:...`, machine-bound) and is never returned by the status API.

- `Fields` (dashboard: the **Column templates** table of the company entry) maps SharePoint **internal** column names to templates. `{property}`
  placeholders resolve case-insensitively against the **top level of the
  document JSON** (any field the export writes is usable). `{property:date}`
  normalizes to `yyyy-MM-dd`. Fields that resolve to empty are skipped.
  `{documentType}` is a handy column value to tell invoices from confirmations
  in one library.
- `HiveBDC.exe --test-sharepoint` verifies auth and the first enabled
  target without uploading anything (the dashboard's **Test SharePoint** does the
  same for the selected company entry).

### Importing column templates

Settings, Document types, select the type and the company entry, then
**Import columns…** in that entry's SharePoint card reads a JSON file and
applies the entry for exactly that type and company (target and column
templates), so nobody types internal column names by hand. One file can carry
every type and company of a customer; each company entry imports its own part
from it. Typically it is prepared from the customer's real library columns
(Graph `GET /sites/{id}/lists/{id}/columns`). **Sample JSON…** next to it saves
a file in this format for the selected type and company: the entry's current
target and columns, examples (`Title = {documentNumber}`, `DocumentDate =
{issueDate:date}`, ...) where they are empty. Edit it and import it back.

```jsonc
{
  "format": "HiveBDC.SharePointFields",
  "version": 1,
  "description": "Customer test sites, 2026-10-07",      // shown nowhere, for the reader
  "documentTypes": [
    { "id": "SalesInvoice",                          // matches the configured type id
      "companies": [
        { "dataAreaId": "001",                       // matches the company entry
          "sharePoint": {
            "siteUrl": "https://contoso.sharepoint.com/sites/Customers",   // optional
            "library": "SalesInvoices",                                    // optional
            "folder": "",                                                  // optional
            "enabled": true,                                               // optional
            "fields": { "Title": "{invoiceNumber}", "DocumentDate": "{issueDate:date}" }
          } } ] } ] }
```

An absent property keeps the configured value; `fields`, when present, replaces
the entry's column templates. Types or companies the file names but the
settings do not have are listed under **Import** and skipped (a company entry
needs its input folder first). The import only fills the form: review it and
**Save**, which runs the normal validation.

## Document mail (optional)

After a successful export the produced file can be mailed via Microsoft Graph. The
recipients, subject and body come from the JSON itself (`recipientEmail`,
`recipientEmailCc`, `emailSubject`, `emailBody`), which the ERP fills from whatever it
already holds for that customer or vendor. A `%1` in the subject is replaced with the
document number; a blank subject becomes `{type display name} {number}`.

**A test system can never mail a customer or vendor.** The AX export stamps each
document with the environment it came from, and only `Live` reaches the real recipient.
Test, Dev, and a document that carries no environment at all are all sent to the **test
recipients** from the settings instead, with `[Test]` on the subject and the addresses it
would have gone to named at the top of the body.

Configure it in the **dashboard → Settings → Document mail**: the sending mailbox, the
client ID and secret, and the test recipients. Mail can be switched off per document type.
**Test mail** checks the app registration and the mailbox without sending anything, and
`HiveBDC.exe --test-mail` does the same from the console.

Prerequisite: an Entra ID app registration with the **application** permission `Mail.Send`
(a separate registration from the SharePoint one). Scope it to the one sending mailbox with
an [application access policy](https://learn.microsoft.com/graph/auth-limit-mailbox-access);
`scripts\New-HiveBDCMailApp.ps1` and `scripts\Grant-HiveBDCMailAccess.ps1` do both.

A failed mail request moves the pair to `<Input>\error\` and
nothing is metered, so dropping it back into the input folder retries it. Missing mail
configuration holds the pair in the input folder instead. A document with
no recipient at all is not an error; it is logged and skipped.

## Settings (`appsettings.json`)

Normally managed from the dashboard, which writes this file atomically and
DPAPI-encrypts the secrets. Schema:

```jsonc
{
  "HiveBDC": {
    "PollSeconds": 30,
    "LogDir": "",                       // blank => %ProgramData%\HiveBDC\logs
    "License": { "File": "" },          // blank => %ProgramData%\HiveBDC\license.lic
    "SharePointAuth": {                 // shared SharePoint app
      "TenantId": "00000000-0000-0000-0000-000000000000",
      "ClientId": "00000000-0000-0000-0000-000000000000",
      "ClientSecret": "enc:... or plaintext"
    },
    "Mail": {                           // shared mail app
      "Enabled": true,
      "TenantId": "",                   // blank => the SharePointAuth tenant
      "ClientId": "00000000-0000-0000-0000-000000000000",
      "ClientSecret": "enc:... or plaintext",
      "From": "rechnung@contoso.de",
      "RedirectTo": [ "test@contoso.de" ],   // non-production documents go here
      "SaveToSentItems": true
    },
    "DocumentTypes": [
      {
        "Id": "SalesInvoice", "DisplayName": "Sales invoice", "Party": "Customer", "Handler": "EInvoice",
        "Enabled": true, "Mail": true,
        "Companies": [
          {
            "DataAreaId": "002",
            "Input": "\\\\server\\share\\HiveBDC\\002",   // shared with the other customer documents
            "Output": "",                                     // blank => <Input>\out\SalesInvoice
            "SharePoint": {
              "Enabled": true,
              "SiteUrl": "https://contoso.sharepoint.com/sites/Documents",
              "Library": "Documents",
              "Folder": "",
              "Fields": { "Title": "{DocumentNumber}", "DocType": "{documentType}", "DocumentDate": "{IssueDate:date}" }
            }
          }
        ]
      },
      {
        "Id": "PurchaseOrder", "DisplayName": "Purchase order", "Party": "Vendor", "Handler": "Pdf",
        "Enabled": true, "Mail": true,
        "Companies": [
          {
            "DataAreaId": "002",
            "Input": "\\\\server\\share\\HiveBDC\\002\\PurchaseOrders",   // own folder (implies the type)
            "Output": "",
            "SharePoint": { "Enabled": true, "SiteUrl": "https://contoso.sharepoint.com/sites/Purchasing",
                            "Library": "Orders", "Folder": "", "Fields": { "Vendor": "{RecipientName}" } }
          }
        ]
      }
      // ... the other built-ins; missing ones are re-added on load (without company
      // entries, i.e. not picked up until configured). Auto-registered types carry
      // "Discovered": true until saved from the dashboard.
    ]
  }
}
```

Env-var overrides use the `HiveBDC__*` prefix; CLI overrides: `--poll <sec>`.
A Hive DocFlow `"HiveDocFlow"` section is still read (with a warning) and renamed on the
first dashboard save.

## CLI

```powershell
HiveBDC.exe                 # run as service / console (continuous poll)
HiveBDC.exe --once          # one-shot scan (exit 1 when any pair failed)
HiveBDC.exe --make-sample   # write one sample per built-in document type to the sales invoice's first input folder
HiveBDC.exe --test-sharepoint
HiveBDC.exe --test-mail     # verify the mail app registration + sender mailbox (sends nothing)
HiveBDC.exe --test-license  # verify the licence (signature, tenant, expiry, features)
HiveBDC.exe --dump-embedded <zugferd.pdf>
HiveBDC.exe install | uninstall           # interactive, elevated
HiveBDC.exe install --quiet [--account DOMAIN\user] [--password ***]
```

## Logging

The service logs every poll cycle, each watched folder, the document types in
play, and each produce / upload / mail step to a rolling daily file (default
`%ProgramData%\HiveBDC\logs`). The **dashboard** logs its own actions into
the **same** file, tagged `UI ·`. The dashboard's log view is colour-coded by
severity — read it there, or open the folder from **All logs → Open log folder**.

## Project layout

```
Balsoft.Hive.BDC.csproj   the service (HiveBDC.exe), one project, folders = namespaces
  Configuration/              BDCOptions (+ migration of older shapes), DocumentTypeOptions,
                              DocumentRoute, SharePointOptions, MailOptions, SettingsFile,
                              SharePointFieldImport (column template import, shared with the dashboard)
  Documents/                  AxDocument (the JSON envelope), DocumentProcessor (scan, resolve,
                              produce, deliver, meter), DocumentHandlers (EInvoice, Pdf),
                              DocumentTypeRegistry (auto-registration), SharePointFieldTemplate
  EInvoice/                   InvoiceMapper (JSON -> EN 16931), EInvoiceGenerator, PdfRepair
  Delivery/                   MailSender (Graph), SharePointUploader (Graph)
  Licensing/                  LicenseValidator (offline, cached per file version)
  Storage/                    ExportLedger (SQLite), SecretProtector (DPAPI)
  Hosting/                    Worker, LocalStatusServer (+ .Config, .Probes), LogSetup, Installer,
                              ServiceAccount, SampleData
  Program.cs                  CLI + host wiring
app/HiveBDC.App/          the WPF dashboard (own project, loopback client only)
tests/Balsoft.Hive.BDC.Tests/   xUnit: mapper, options migration, routing, templates, mail, library key
build/                        publish-and-sign.ps1, EULA.txt, wizard art
setup/HiveBDC.Setup/      Setup.exe: Hive BDC's product data on Balsoft.Hive.Installer.Windows
```

Hot paths are deliberately cheap: one JSON parse per document (DTO and column
templates share it), the licence is verified once per file version and day
rather than on every poll, `/metrics` reads on one SQLite connection, and
`/logs` tails the file from the end instead of reading the whole day.

## Build / publish

```powershell
dotnet build
dotnet test
# Full signed release (service + dashboard + Setup + update feed manifest), with PowerShell 7:
pwsh build\publish-and-sign.ps1
```

Release binaries and the installer are Authenticode-signed with the Balsoft GmbH
OV code-signing certificate (private key in Google Cloud KMS, RFC3161 timestamp);
signing is the last packaging step per artifact.

GitHub Actions (`.github/workflows/ci.yml`) builds and tests every push, pull
request and tag. It has no secrets and no cloud access: signed releases are built
and published from Balsoft's own build server, never from GitHub.

## Notes

- ZUGFeRD hybrids are PDF/A-3b. TrueType fonts the carrier uses without embedding
  them (AX 2009 prints plain "Arial" for regular and bold) are embedded from the
  server's font folder, picked by the descriptor's bold/italic flags, and trailing
  .notdef glyphs AX 2009 writes for line breaks are removed. Validate with veraPDF
  when the carrier source changes.
- E-invoices are deliberately **not** digitally signed: §14 UStG has not required
  a signature since 2011, and EN 16931 / XRechnung / ZUGFeRD add no signature
  requirement. Do not add XAdES/PAdES signing.
- PDF pass-through documents are delivered byte-for-byte as the ERP printed them;
  the service adds nothing to them.
- Stack: `Balsoft.Hive.EInvoice` + `Balsoft.Hive.EInvoice.Pdf` (Balsoft's own EN 16931
  library, open source, github.com/balsoft-gmbh/Balsoft.Hive.EInvoice: CII XML, pre-flight
  rule check, PDF/A-3 hybrids on PDFsharp), `Microsoft.Data.Sqlite` (export ledger),
  `Serilog` (logging), `CommunityToolkit.Mvvm` (dashboard). All Apache-2.0 / MIT —
  see THIRD-PARTY-NOTICES.md.
- Every e-invoice runs through the library's pre-flight check (EN 16931 and XRechnung
  rules with their official ids, e.g. `[BR-DE-15]`, `[PEPPOL-EN16931-R010]`). Findings
  are logged as warnings and the document is still written, so a gap in the ERP data
  is visible in the log without stopping delivery.

### Pending input and delivery recovery

An explicit `dataAreaId` must match a configured company for the document type,
including when the file arrives in another company's folder. Without a company
ID, routing must identify one company unambiguously. Unresolved routes remain
in the input folder until configuration is corrected.

A required PDF that has not arrived, a locked input file, or an incomplete JSON
written within the last 30 seconds remains pending. Older malformed JSON goes to
`error`. Producers should finish and close the PDF first, then publish complete
JSON using an atomic rename from a temporary file. The service cannot distinguish
a valid but unfinished PDF written by a producer that closes it between chunks.

If SharePoint is enabled for the route, or mail is enabled globally and for the
type, an unavailable delivery client holds the document before generating or
sending anything. Correcting the connection settings resumes processing on the
next scan. Explicitly disabled delivery remains optional. Network errors during
an actual delivery still go to `error` for operator-managed retry; successful
external deliveries are not yet tracked as independently resumable steps.

A failed scan or quarantine operation is logged without blocking other documents
or folders. Cycle summaries include held documents rather than reporting idle.
Migration from legacy company configuration preserves explicit output folders;
a per-type output override takes precedence.
