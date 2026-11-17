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
service only over a loopback (`127.0.0.1`) API: a minimal ASP.NET Core (Kestrel)
server on a port the system picks, with a bearer token; port and token are in
`%ProgramData%\HiveBDC\app-endpoint.json`, and a request from any other address
is refused.

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
then go to `error\` with that reason. A built-in type you remove (**Remove** in
Settings) stays removed: it is not added back on the next start or save, and a
pair the ERP still sends for it goes to `error\` instead of being registered
again. Removing every type is refused; disable them instead. An input folder
that only disabled types point at is not scanned at all, so the installer
default on an unused type causes no errors.

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
  // invoice notes (BT-22)
  "notes": "...",                       // general note, written without a subject code
  "legalNotice": "...",                 // legal form, seat, register court and number, managing directors
                                        // -> note with subject code REG (BT-21); validators look for it
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
| Project name (CII, next to BT-11) | `projectName`, `projektname`, `projektbezeichnung` (written only with a project reference; without a name the XML keeps "Project reference") |
| BT-12 Contract reference | `contractReference` (the Aktivbank `zrKennzeichen` wins) |
| BT-13 Purchase order reference | `purchaseOrderReference`, `purchOrderReference`, `purchOrderFormNum`, `purchase order reference`, `customerOrderNumber`, `bestellnummer` |
| BT-14 Sales order reference | `orderReference`, `salesOrderNumber`, `auftragsnummer` |
| BT-15 / BT-16 Receiving / despatch advice | `receivingAdviceReference`, `deliveryNoteNumber`, `packingSlipNumber` |
| BT-17 Tender or lot | `tenderReference` |
| BT-19 Buyer accounting reference | `buyerAccountingReference`, `costCenter` |
| BT-25 / BT-26 Preceding invoice | `precedingInvoiceNumber`, `precedingInvoiceDate` |
| BT-72 Delivery date | `deliveryDate`, `lieferdatum` (else the issue date) |
| BG-13 Deliver to (BT-70, 75, 77, 78, 80) | `deliveryName`, `deliveryStreet`, `deliveryCity`, `deliveryPostcode`, `deliveryCountry` (country falls back to the buyer's; BT-80 is required for intra-community supplies, BR-IC-12) |
| BT-83 / BT-85 / BT-86 Payment | `paymentReference`, `bankAccountHolder`, `bankSwift` / `bic` |
| BT-99 / BT-104 Document level charge | `chargeTotalAmount` (the sum of the ERP's header charges: freight, small-quantity surcharge, ...), `chargeReason` (default "Zuschläge"); one header charge taxed with the header `vatCategory` / `vatRate`, counted in BT-108, BT-109 and the VAT breakdown. A negative value on a plain invoice is a document level allowance ("Rabatt"); credit notes take the absolute value |
| Line BT-127, 132, 133 | `lineNote`, `orderLineReference`, `costCenter` |
| Line BT-155, 156, 157, 159 | `itemId` / `sellerItemId`, `customerItemId`, `gtin` / `ean`, `originCountry` |
| Line BT-149 / BT-146 price base quantity | `priceUnit` (an AX price unit such as 100) with `priceUnitPrice` (the price per that quantity); without the latter the per-piece `unitPrice` is scaled |
| Line BT-141 / BT-144 charge | `lineChargeAmount`, `lineChargeReason` (an AX `SalesMarkup` the ERP folded into its line amount) |
| Line BT-136 / BT-139 allowance | `lineDiscountAmount`, `lineDiscountReason` |

When a term arrives under several keys, the first non-empty one in the JSON wins.
BT-13 is the exception: `purchOrderReference` wins, then `purchaseOrderReference`,
then the first other purchase order key.

Without `buyerReference` or a purchase order key, `reference` fills BT-10 and
BT-13 as before, except that a `reference` equal to the invoice number never
becomes BT-13 (the log says so). The log names every term taken from such a key and lists the
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

## Word input (filled Word invoices instead of an ERP export)

A company entry can take **Word documents** instead of ERP export pairs (**Settings > Outbound >** a document type **>** a company **>
Comes from: Word**, or `InputFormat: "Word"` on the entry). Hive BDC then reads every `.docx` in the
input folder, builds the same JSON an ERP export would drop, renders the PDF itself and runs the normal pipeline:
XRechnung or ZUGFeRD, SharePoint, mail, ledger. Local folders and network shares work the same way as for JSON (a
share needs the service under a domain account).

**Template.** Every value sits in a Word content control whose **Tag** is its EN 16931 business term (`BT-1`, `BT-44`,
`BT-153`, ...) or a key of the ERP JSON (`documentType`, `recipientEmail`, `emailSubject`, ...). Nothing is read by
position or label. The invoice lines are a **repeating section** tagged `lines`; each item holds the line tags
(`BT-126`, `BT-153`, `BT-154`, `BT-129`, `BT-130`, `BT-146`, `BT-152`, `BT-131`). The control tagged `eInvoiceType`
(a drop-down with the values `xrechnung` and `zugferd`) decides the format; without it, XRechnung is produced, as for
a JSON without `eInvoiceType`. Date pickers deliver their ISO date; typed dates are read as `dd.MM.yyyy`. Amounts are
read in German notation (`721.613,72 EUR`, `19,0 %`). A control that still shows its placeholder counts as empty.
**Word template...** in Settings saves a generic template; **Check a Word file...** shows what Hive BDC reads from a
file, the totals check and a PDF preview, without touching the service.

**Checks before anything is produced.** BT-1, BT-2, BT-44 and one line are required; XRechnung also needs BT-10 and
BT-49. When the document prints totals (`BT-109`, `BT-110`, `BT-112`), they must equal the sums of the lines to the
cent, or the document is not converted.

**PDF.** A PDF with the same name next to the `.docx` is used as it is. Otherwise Hive BDC renders the document with
its own renderer (PDFsharp): page size and margins, header and footer, paragraphs with tab stops and line spacing,
tables including nested tables, merged cells, borders and shading, inline images, content controls, `PAGE` and
`NUMPAGES`. Text boxes, shapes, floating images, text columns, lists and embedded objects are not drawn: such a
document goes to `error\` unless a PDF is saved with it. Fonts come from the server's font folder; a font that is not
installed is replaced by Arial and logged. Word's AutoFit column widths are not recalculated; the table grid stored in
the document is used.

**Folders.** A Word file is read once it is closed and has not changed for 10 seconds; Word's `~$` lock files are
skipped. The `.docx`, the JSON read from it and the PDF move together to `processed\` or `error\`. A failed file gets
`<name>.docx.error.txt` next to it (German and English), because the people who type invoices may not see the
dashboard. A `.doc` (Word 97-2003) cannot carry the fields and goes to `error\` with that reason.

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

A local install checks the tenant against the SharePoint app's tenant in the
settings. An Azure instance checks it against the tenant of its own identity (the
`tid` of its managed identity's access token), which the settings cannot change;
while that tenant cannot be determined, the instance takes no documents and a
tenant-bound licence is not accepted. An Azure instance keeps its licence as
`license.lic` next to its settings blob (the app folder is read-only there); the
licence is checked against the instance's tenant before it is stored, and a
foreign, expired or tampered one never replaces a working one.

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
**application** permission `Sites.Selected` granted on the sites (or
`Sites.ReadWrite.All`) and a client secret; `scripts\New-HiveBDCSharePointApp.ps1` and
`scripts\Grant-HiveBDCSharePointAccess.ps1` set it up (see
[App registrations](#app-registrations)). The secret is stored **DPAPI-encrypted**
(`enc:...`, machine-bound) and is never returned by the status API.

- `Fields` (dashboard: the **Column templates** table of the company entry) maps SharePoint **internal** column names to templates. `{property}`
  placeholders resolve case-insensitively against the **top level of the
  document JSON** (any field the export writes is usable). `{property:date}`
  normalizes to `yyyy-MM-dd`. Fields that resolve to empty are skipped.
  `{documentType}` is a handy column value to tell invoices from confirmations
  in one library.
- `HiveBDC.exe --test-sharepoint` verifies auth and the first enabled
  target without uploading anything (the dashboard's **Test this library** on a
  company's SharePoint library screen does the same for that company).

### Importing column templates

Settings > Outbound > a document type > a company > SharePoint **Edit**, then
**Import columns…** on the SharePoint library screen reads a JSON file and
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

Prerequisite: an Entra ID app registration (separate from the SharePoint one) that may send
as exactly one mailbox, through the Exchange role `Application Mail.Send` limited to that
mailbox; `scripts\New-HiveBDCMailApp.ps1` and `scripts\Grant-HiveBDCMailAccess.ps1`
set it up (see
[App registrations](#app-registrations)).

A failed mail request moves the pair to `<Input>\error\` and
nothing is metered, so dropping it back into the input folder retries it. Missing mail
configuration holds the pair in the input folder instead. A document with
no recipient at all is not an error; it is logged and skipped.

## App registrations

Hive BDC uses two app registrations in the customer tenant, one for SharePoint and one
for mail, so each holds only the permission it needs. Each is set up in two steps, which
different administrators can do:

1. **Create the app registration**, without any permission: `New-HiveBDCSharePointApp.ps1`
   creates "Hive BDC SharePoint", `New-HiveBDCMailApp.ps1` creates "Hive BDC Mail". Each prints
   its client ID. Role: Application Administrator.
2. **Assign the permissions** to that client ID:

| Script | Permission | Narrowed to | Role |
|---|---|---|---|
| `Grant-HiveBDCSharePointAccess.ps1 -AppId ... -SiteUrl ...` | `Sites.Selected` with admin consent (`-AllSites`: `Sites.ReadWrite.All`) | write on each `-SiteUrl` | Global or Privileged Role Administrator, SharePoint Administrator |
| `Grant-HiveBDCMailAccess.ps1 -AppId ... -SenderMailbox ...` | Exchange role `Application Mail.Send`, no Entra consent | the one `-SenderMailbox` | Exchange Administrator (Organization Management) |

The scripts run in **Azure Cloud Shell (PowerShell)**, which has Azure CLI and the needed
modules ready, or in any PowerShell 5.1+ with Azure CLI. Each shows the tenant and asks
before changing anything, supports `-WhatIf`, and skips what already exists, so a second
run is safe. None of them creates or stores a client secret; step 1 prints how.

Azure CLI creates the apps and grants the SharePoint consent. It cannot grant a single site
or manage Exchange (Graph and SharePoint both refuse its token for that), so step 2 signs in
once more: the site grant asks for `Sites.FullControl.All` through Microsoft's public client
"Microsoft Graph Command Line Tools" (plain REST, no Graph module), and the mailbox
restriction uses `ExchangeOnlineManagement`. Outside Cloud Shell that sign-in opens a browser;
in Cloud Shell it uses a device code, which many tenants block with Conditional Access. For
those tenants run step 2 on the admin's own computer (PowerShell with Azure CLI, plus
`Install-Module ExchangeOnlineManagement` for the mail script).

Custom Exchange scopes need `Enable-OrganizationCustomization` once per tenant; the mail script
turns it on when it is off. Exchange applies it with a delay of up to about an hour, so a first
run can stop with a "run again later" message; the rerun skips everything already done.

The mail app deliberately gets **no** `Mail.Send` consent in Entra ID. Exchange RBAC for
Applications adds to an Entra consent instead of narrowing it, so a consented app could still
send as every mailbox. The script removes such a consent if it finds one. Only tenants without
RBAC for Applications fall back to an Entra consent restricted by an application access policy.

```powershell
# in Azure Cloud Shell (PowerShell) of the customer tenant
$base = 'https://balsoft.de/downloads/hive-bdc'
foreach ($f in 'New-HiveBDCSharePointApp', 'Grant-HiveBDCSharePointAccess', 'New-HiveBDCMailApp', 'Grant-HiveBDCMailAccess') {
    Invoke-WebRequest "$base/$f.ps1" -OutFile "$f.ps1"
}

./New-HiveBDCSharePointApp.ps1          # step 1, prints the client ID
./Grant-HiveBDCSharePointAccess.ps1 -AppId <client-id> -SiteUrl https://contoso.sharepoint.com/sites/Invoices
./New-HiveBDCMailApp.ps1                # step 1, prints the client ID
./Grant-HiveBDCMailAccess.ps1 -AppId <client-id> -SenderMailbox rechnung@contoso.de
```

The same scripts, with a step-by-step guide for the customer's administrator, are on the
Hive BDC page at balsoft.de. Then enter tenant ID, client IDs and secrets in the dashboard
under **Settings, Connections**.

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
            "Mail": true,                                     // this company mails; absent => the type's "Mail"
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
      // ... the other built-ins you use. A built-in left out stays out; only a file with
      // no types at all gets the six built-ins on load (without company entries, i.e.
      // not picked up until configured). Auto-registered types carry "Discovered": true
      // until saved from the dashboard.
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
HiveBDC.exe --install-license <file.lic>  # check a licence and store it (the licence file locally, license.lic next to the settings blob in Azure)
HiveBDC.exe --dump-embedded <zugferd.pdf>
HiveBDC.exe --word-read <invoice.docx>             # the JSON, totals and problems Hive BDC reads from a Word invoice
HiveBDC.exe --word-pdf <invoice.docx> [out.pdf]    # render a Word invoice with the built-in renderer
HiveBDC.exe --word-layout <invoice.docx>           # print the layout (paragraph and row heights) for support
HiveBDC.exe --word-template <out.docx>             # write the generic Word invoice template
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
                              DocumentTypeRegistry (auto-registration), SharePointFieldTemplate,
                              BlobStager, ServiceBusStager and HttpsInbound (Azure sources staged into the input folder)
  EInvoice/                   InvoiceMapper (JSON -> EN 16931), EInvoiceGenerator, PdfRepair
  Delivery/                   MailSender (Graph), SharePointUploader (Graph)
  Connectors/                 IDocumentTarget, ConnectorRegistry (target kinds by name), SharePoint, mail, DocuWare,
                              blob, HTTPS and Service Bus targets, DocumentFile (a produced file as name, content type and content)
  Licensing/                  LicenseValidator (offline, cached per file version), LicenseTenant (the tenant a
                              licence must name: the settings locally, the instance identity in Azure),
                              LicenseStore (where the licence lives: a file locally, a blob in Azure)
  Storage/                    IStateStore, ExportLedger (SQLite: exports, deliveries, document history),
                              TableStateStore (the same in Azure Table Storage, for the Azure run mode;
                              AzureTableRows behind ITableRows), SecretProtector (DPAPI locally, "kv:" Key Vault
                              references in Azure; KeyVaultSecretVault), SettingsBlob (settings of an
                              Azure instance: AzureETagBlob, BlobSettingsDocument, BlobSettingsProvider)
  Hosting/                    Worker, RunMode (run modes, Azure settings check, processing gate), AzureWebHost
                              (Azure: App Service sign-in, roles, health),
                              LocalStatusServer (+ .Config, .Probes; Kestrel on 127.0.0.1), ApiCall, LogSetup, Installer,
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
an actual delivery still go to `error` for operator-managed retry. Each successful
delivery is recorded per target, so a target that creates a new document on every
call (a document management system) is handed the id of its earlier delivery on a
retry and updates that document instead of storing a second one.

### Targets

A company entry delivers to its SharePoint block (when on), then to every enabled
entry of its `Targets` list, then by mail when the document type mails. A target has
a `Kind`, an optional `Connection`, `Settings` for the kind and `Fields` with the same
templates as the SharePoint columns. A kind the installed version does not know holds
only that company entry with a message; every other entry keeps running. An entry
without further targets is saved without the `Targets` key, so the file stays
readable by earlier versions, and a save from a dashboard that does not send targets
keeps the stored ones.

A target that says "try again later" (DocuWare busy, an HTTPS endpoint that answers
408, 429 or 5xx or does not answer) leaves the document waiting in the input folder;
the next scan tries again. Any other refusal sends it to `error\` with the reason.

### HTTPS target (Logic App, Power Automate, own API)

A target of kind `https` posts each produced document as JSON to the address of its
`https` connection, plus the optional `Settings.Path`:

```json
{ "documentId": "...", "documentNumber": "RE-1", "documentType": "SalesInvoice", "dataAreaId": "1000",
  "fileName": "RE-1.pdf", "contentType": "application/pdf", "content": "<base64>",
  "fields": { "number": "RE-1" }, "previousId": "only on a repeat" }
```

`fields` are the target's `Fields` templates filled from the document. When the
endpoint answers `{"id": ...}`, that id is kept and sent as `previousId` if the same
document is delivered again, so the receiver can update instead of adding. The
connection holds the address (`Settings.Url`), the header for the key
(`Settings.HeaderName`, default `x-api-key`) and the key as its secret. For a Logic
App or Power Automate trigger, leave the address empty and keep the whole trigger URL
as the secret: its `sig=` part is a key, and log lines and the document history name
only host and path. The connection test asks the address with `GET` and sends nothing.

### Settings

Settings opens on four entries: **Outbound** (documents from the ERP), **Inbound**
(documents into the ERP), **Connections** and **Service**. Every setup is a flow of one
document type in one direction, with its companies. Each screen holds one level and the
adding and removing of the level below it; a breadcrumb leads back:

- **Outbound** lists the document types the ERP sends. Add a type here, or remove one.
- **A document type** holds its name, the ID the ERP writes, who it is sent to and what
  Hive BDC makes (an e-invoice or the PDF as the ERP made it), and lists its companies.
  Add or remove a company here.
- **A company** holds where its documents come from (the ERP export, JSON and PDF, or
  Word documents, and the folder) and where they go: SharePoint, one row per DocuWare
  file cabinet, and mail. The company has its own switch (`"Enabled"` on the company
  entry; a file without it keeps the company on): a company that is off is not read for
  that type, and a document for it that still arrives goes to `error\` with the reason.
  Each destination has its own switch and keeps its settings when switched off. Mail
  needs Document mail under Connections.
- **Inbound** lists the document types brought into the ERP, the same way: a type, its
  companies, and per company where its documents come from and where the ERP reads them
  (see Inbound flows below).
- **+ Add a destination** (on a company) shows one tile per destination kind this version
  delivers to: SharePoint library, DocuWare file cabinet, Azure Blob container. A further
  kind is one connector in the service and shows up here without a dashboard change. A
  kind whose connection is not set up says Connect first and opens it under Connections.
- **A destination** opens as its own screen: its settings, with lists read from the
  system itself (the DocuWare file cabinet, the SharePoint library), and for systems
  with fields one row per field with the template that fills it.
- **SharePoint library** (from a company) holds the site, library, folder and columns.
- **Connections** shows SharePoint, DocuWare and Document mail, plus one card per further
  connection kind of this version (Azure Storage today), each opening its own screen with
  **Test connection**.

Remove asks in the card before anything goes, and nothing is written until **Save
changes**.

Mail is set per company (`"Mail"` on the company entry). A file written before 1.5.0
has it on the document type only; its companies follow the type until the first save
from Settings writes each company's own value.

### DocuWare target

In the dashboard:

1. **Settings > Connections > DocuWare:** address, user name and password, then
   **Test connection**. It signs in and lists the file cabinets the user can see.
2. **Settings > Outbound >** a document type **>** a company **> Goes to >
   + Add a destination > DocuWare file cabinet:** choose the file cabinet by name. Its fields
   are listed with their kind and whether they are required; fill the ones you want with
   templates. Choose the field that holds the document number under **Never store twice**.
3. **Save.** Every document of that type and company now also goes to DocuWare.
4. **View > Document history** shows what needs attention (stopped or waiting
   documents) and every step of one document by its number; **Retry** puts a stopped
   document back into its input folder.

The settings it writes:

A DocuWare connection is defined once under `Connections` and used by any number of
targets:

```json
"Connections": [
  { "Id": "docuware-main", "Kind": "docuware",
    "Settings": { "Url": "https://yourcompany.docuware.cloud", "UserName": "integration-user" },
    "Secret": "" }
]
```

The address may be copied from the browser (the web client address works). The
password is entered in the dashboard and stored protected on the machine; the
settings API never returns it. Hive BDC signs in through DocuWare's identity
service, which is the only sign-in the DocuWare platform still accepts, and the
DocuWare user must be allowed to sign in with a DocuWare password.

A target of kind `docuware` names the connection and the file cabinet:

```json
{ "Kind": "docuware", "Connection": "docuware-main",
  "Settings": { "FileCabinet": "<file cabinet id>", "MatchField": "DOCUMENT_NUMBER" },
  "Fields": {
    "DOCUMENT_NUMBER": "{documentNumber}",
    "DOCUMENT_DATE": "{invoiceDate}",
    "NET_AMOUNT": "{netAmount}",
    "DELIVERY_NOTES": "{deliveryNoteNumbers}",
    "ITEMS.QUANTITY": "{lines.quantity}" } }
```

Field names are the cabinet's database names. Values are typed by the cabinet's
own field list: text (length checked), numbers in German or English notation,
dates, keyword fields (values separated by `;` or line breaks) and table fields,
where `TABLE.COLUMN = {list.property}` adds one row per element of that list in the
document. A field the cabinet does not have, a value that does not fit, or a
required field without a value stops the delivery with the field named, and
nothing is stored. `MatchField` (optional) names the field that holds the document
number: before storing, Hive BDC looks for a document with that value and updates
it, so a document is never stored twice. A refused sign-in or an unreachable
DocuWare keeps the document waiting and is tried again on the next scan. The
target needs the licence feature `sharepoint`.

### Azure Blob Storage (D365 F&O)

The F&O model sends each document's JSON as a blob without extension and its PDF as
a second blob that the JSON names in `pdfLink`. A company entry with a `Source` of
kind `blob` takes them from there:

```json
{ "DataAreaId": "USMF", "Input": "D:\\HiveBDC\\staging\\usmf", "Output": "",
  "Source": { "Kind": "blob", "Connection": "fo-storage",
    "Settings": { "Container": "bdc-out", "Prefix": "SalesInvoice/",
                  "ProcessedPrefix": "processed/SalesInvoice/", "ErrorPrefix": "error/SalesInvoice/" } } }
```

Before every scan, each new JSON blob under the prefix and its PDF are downloaded
into the input folder as `{id}_blob.json` and `{id}_blob.pdf`, and the normal
pipeline processes them. A note per pair in the folder's `.hive-sources` records
where it came from; once the pair is in `processed` or `error`, its blobs are moved
to the processed or error prefix (defaults: `processed/` and `error/` in front of the
source prefix). The notes are files, so a restart loses nothing, and a pair is never
downloaded twice. A `pdfLink` that points outside the source container is not
fetched; the document waits with that reason in its history.

A target of kind `blob` uploads the produced file to `Container` under `Prefix`, for
an F&O receive flow; with `"Status": "true"` it also writes
`{prefix}{document id}.status.json` with number, type, company and file name.

An `azure-storage` connection holds the storage connection string as its secret, or,
in Azure, the account address in `Settings.AccountUrl`, signing in with the managed
identity.

### Azure Service Bus (D365 F&O)

A company entry with a `Source` of kind `servicebus` takes the documents an F&O send
class (BDCDataSend_ServiceBusQueue or _Topic) puts on a queue or topic: the message
body is the document JSON, its `MessageId` the outbound id, and the PDF is the blob
the JSON names in `pdfLink`:

```json
{ "DataAreaId": "USMF", "Input": "D:\\HiveBDC\\staging\\usmf-sb", "Output": "",
  "Source": { "Kind": "servicebus", "Connection": "fo-bus",
    "Settings": { "Queue": "bdc-out", "PdfConnection": "fo-storage", "PdfContainer": "bdc-pdf" } } }
```

(`Topic` and `Subscription` instead of `Queue` for a topic.) Before every scan, new
messages are received with peek-lock, staged into the input folder as `{id}_sb.json`
and `{id}_sb.pdf`, and deferred: a deferred message holds no lock and its delivery
count does not grow, so a document that waits for hours is not dead-lettered by the
broker. Once the pair is in `processed`, the message is completed; once it is in
`error`, it is dead-lettered (`HiveBDC.DocumentError`) with the last failed step of
the document history as its description. A message whose `MessageId` was staged
before is completed without a second document. A `pdfLink` outside `PdfContainer`,
or one without `PdfConnection` and `PdfContainer`, is dead-lettered
(`HiveBDC.Refused`); a PDF that is not there yet leaves the message for a later scan.

A target of kind `servicebus` (`Settings.Queue` or `Settings.Topic`) sends each
produced document as the same JSON as the HTTPS target, with `MessageId`
`{type}_{company}_{document id}` (the same for a repeat, so the queue's duplicate
detection can drop it), the document type as `Subject` and `documentNumber`,
`documentType`, `dataAreaId` and `fileName` as message properties; an F&O receive
flow reads the body as UTF-8 text. A transient Service Bus failure leaves the
document waiting. A `servicebus` connection holds the namespace connection string as
its secret, or, in Azure, `Settings.Namespace` (`{name}.servicebus.windows.net`) for
the managed identity.

### Azure run mode (in development, 1.8.0)

The same program runs as an Azure web app. The deployment sets, as app settings:

| Setting | Meaning |
|---|---|
| `HiveBDC__RunMode` | `azure` (default `local`) |
| `HiveBDC__Azure__TableEndpoint` | `https://{account}.table.core.windows.net`; the state (exports, deliveries, document history) lives there, signed in with the managed identity |
| `HiveBDC__Azure__StorageConnection` | instead of the endpoint, a storage connection string (as an App Service Key Vault reference) |
| `HiveBDC__Azure__TablePrefix` | table name prefix, default `HiveBDC` |
| `HiveBDC__Azure__KeyVaultUrl` | `https://{vault}.vault.azure.net`; every secret in the settings is then `kv:{secret name}` |
| `HiveBDC__Azure__SettingsBlobUrl` | `https://{account}.blob.core.windows.net/{container}/{name}.json`; the settings of the instance, in place of the package's `appsettings.json` |
| `HiveBDC__Azure__SettingsPollSeconds` | how often the settings blob is checked for a change, default 15 (at least 5) |

The settings blob has the same shape as `appsettings.json` (section `HiveBDC`). The
service checks its ETag every poll interval and reloads it when it changed. A
document type the service registers by itself is written back to the blob only if
nobody wrote it in between; otherwise it reads the blob again and makes its change
on that version. A missing blob means empty settings. Without a settings blob the
package's `appsettings.json` is used; it is read-only in Azure, so a type the service
registers lasts for that run only.

In Azure there is no loopback API for the Windows app. Instead the service runs a web
host on the port App Service gives (`PORT`, default 8080), behind App Service
authentication with Microsoft Entra ID:

- `/healthz` answers without sign-in (`{"status":"ok","version":...}`);
- everything else is served only while App Service authentication is on
  (`WEBSITE_AUTH_ENABLED`); otherwise the answer is 503, whatever the request says;
- the signed-in user (the `X-MS-CLIENT-PRINCIPAL` header, which only App Service
  can set) must belong to the instance's own tenant and hold an app role of the
  instance's app registration: `Viewer` reads, `Operator` may also retry a document,
  `Administrator` may change settings, connections and the licence;
- the calls are the same as the local API's (`/status`, `/config`, `/history`, ...);
  settings are saved into the settings blob, secrets into Key Vault, and every change
  is logged with the user who made it.
- `POST /inbound/{documentType}/{company}` is the HTTPS source: a company entry with
  a `Source` of kind `https` takes documents an ERP, a Logic App or a Power Automate
  flow posts there, as `{"document": {the ERP export JSON}, "pdf": "base64"}`. The
  caller sends the key of the entry's `https` connection in its header (default
  `x-api-key`; it needs no Entra sign-in, so the deployment excludes `/inbound` from
  App Service authentication). `?id=` names the document, else its `documentNumber`.
  The answer is 202 when the document is staged, 200 when it arrived before (no
  second copy), 401 for a missing or wrong key, 413 above 50 MB, 400 for another
  body and 404 when no company of that type takes HTTPS; nothing is stored on a
  refusal.

The service takes no document
from a source while one of these holds, and says why in the log (when the reason
changes, then every 5 minutes):

- the settings blob cannot be read (the last settings read stay in use);
- the settings use what only a local install has: a company that reads a folder
  instead of Blob Storage or Service Bus, Word input, a company that only writes to
  its output folder, an inbound flow that writes to a folder, or a secret kept in the settings
  instead of a `kv:` reference. Each is named with its type and company or flow;
- Table Storage cannot be reached;
- Key Vault cannot be reached (asked at most once a minute);
- the tenant of the instance, which the licence is bound to, cannot be determined.

Without a table endpoint or connection string, or with an unknown run mode, the
program does not start (exit code 3); `--once` exits with 2 while it would wait.

### Inbound flows (DocuWare, SharePoint)

**Settings > Inbound > + New document type**, then **+ Add company** asks where the
documents come from: one tile per document system this version reads from (DocuWare and
SharePoint today; a further system is one source connector in the service and shows up
here without a dashboard change). The company's screen then shows that system's own
settings, with lists read from the system itself:

- **DocuWare:** the file cabinet, the field (for example `STATUS`) and the value that mark
  a document ready, and the value written once it is imported.
- **SharePoint:** the site, the document library, the column and the value that mark a
  file ready, and the value written once it is imported. It uses the SharePoint app from
  Connections (application permission Sites.ReadWrite.All, or Sites.Selected with write
  on that site). SharePoint filters best on an indexed column; in a library with more
  than 5000 files, index the ready column (Library settings, Indexed columns), otherwise
  the flow says so and takes nothing.

Every scan, the ready documents become a CSV file in the folder the ERP reads, and the
ready field or column is then set to the done value, so a document is imported once.
The document's file can be saved next to the CSV (`{FilePath}` is its path). Columns are
named as the ERP expects; **Columns from a CSV the ERP reads…** takes them from the first
line of such a file, **One column per field** adds every field, and **Preview with a ready
document** reads one ready document so the values can be checked. Each column is filled
from a template: `{FIELD}` (a field by its internal name), `{TABLE.COLUMN}` (a column of
the current table row when the rows come from a DocuWare table field), `{DWDOCID}` or
`{ID}` (the document's id), `{DataAreaId}` (the company) and `{FilePath}`. With a table
chosen, every table row becomes a CSV row with the other columns repeated. Two switched-on
flows that would pick up the same documents are refused on Save.

The CSV is UTF-8 with a byte order mark, separated by `;` unless set otherwise,
with numbers and dates in German (`1234,5`, `31.12.2026`), English (US) or ISO
format, quoted per RFC 4180 where a value holds the separator, a quote or a line
break, and written under a temporary name first. Dates are read in German time, so a
date stored at midnight is not moved to the day before. A column naming a field the
document lacks stops that document with the column named and nothing written; it is
tried again after an hour or after the flow is saved. If the done mark cannot be
written, the next scan only repeats the mark. Each company is one entry under `Flows`;
`DocumentType` and `DataAreaId` are new in 1.5.0, and an entry without them is shown under
its own name:

```json
"Flows": [
  { "Id": "purchase-order-demf", "Name": "Purchase order (DEMF)", "Enabled": true,
    "DocumentType": "Purchase order", "DataAreaId": "DEMF",
    "Source": { "Kind": "docuware", "Connection": "docuware",
      "Settings": { "FileCabinet": "<id>", "ReadyField": "STATUS", "ReadyValue": "Released", "DoneValue": "Imported" } },
    "Produce": { "Kind": "csv",
      "Settings": { "Delimiter": ";", "Culture": "de-DE", "RowsFrom": "ITEMS", "FileName": "{DWDOCID}.csv" },
      "Columns": [ { "Name": "DWDOCID", "Value": "{DWDOCID}" }, { "Name": "ItemId", "Value": "{ITEMS.ITEM_NUMBER}" } ] },
    "Targets": [ { "Kind": "folder", "Settings": { "Folder": "\\\\server\\erp\\pending", "SaveFile": "true" } } ] },
  { "Id": "vendor-invoice-demf", "Name": "Vendor invoice (DEMF)", "Enabled": true,
    "DocumentType": "Vendor invoice", "DataAreaId": "DEMF",
    "Source": { "Kind": "sharepoint",
      "Settings": { "SiteUrl": "https://contoso.sharepoint.com/sites/Finance", "Library": "Incoming invoices",
                    "ReadyColumn": "Status", "ReadyValue": "Approved", "DoneValue": "In ERP" } },
    "Produce": { "Kind": "csv", "Settings": { "FileName": "{ID}.csv" },
      "Columns": [ { "Name": "Company", "Value": "{DataAreaId}" }, { "Name": "VendorAccount", "Value": "{VendorNumber}" } ] },
    "Targets": [ { "Kind": "folder", "Settings": { "Folder": "\\\\server\\erp\\vendor-invoices", "SaveFile": "true" } } ] }
]
```

### Document history

Every document gets a history in `hive.db` (table `events`): received, read,
produced, one line per target (or "skipped", for example a mail without recipient),
waiting, failed and done, in plain words. Mail switched off in Settings writes no line.
A waiting document's reason is written once, not on every scan. Mail addresses are
masked, line breaks flattened and a line is capped at 2000 characters. The history is
pruned with the export ledger. The loopback API serves it as
`GET /history?search=<text>&limit=<n>`: every step of the documents (at most 20) whose
number, id or input file name contains the text, in any case; without `search` it
returns the newest lines of all documents. `GET /history/attention` lists the documents whose
last line failed or is waiting, and `POST /history/retry` with `{"documentId": "..."}`
moves that document's files from the error folder back into its input folder.

A failed scan or quarantine operation is logged without blocking other documents
or folders. Cycle summaries include held documents rather than reporting idle.
Migration from legacy company configuration preserves explicit output folders;
a per-type output override takes precedence.
