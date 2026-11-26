# Sending invoice data to Hive BDC: the JSON fields for the e-invoice

This guide is for the developer who writes the ERP export (AX 2009, AX 2012, D365FO or any other system). It lists
every JSON key Hive BDC reads for an invoice and the EN 16931 business term (BT) it fills, so the XRechnung XML and
the XML inside a ZUGFeRD PDF come out complete and valid. Valid from Hive BDC 1.15.0.

## How the export works

The ERP writes one JSON file per document into the input folder, and for ZUGFeRD also the PDF with the same name.
Hive BDC maps the JSON to the EN 16931 model, writes the XML and delivers it.

Rules that apply to every key:

- Key names ignore case, spaces, `-`, `_` and `.`: `senderContactEmail`, `SenderContactEmail`, `sender contact email`
  and `sender_contact_email` are the same key. Every term also takes its BT number as key (`"BT-43": "..."`).
- An empty value is the same as a missing key. Send only what the invoice has.
- Values are strings. Numbers may also be JSON numbers.
- Dates: `yyyy-MM-dd` (preferred), `dd.MM.yyyy`, `yyyy/MM/dd` or `yyyyMMdd`.
- Amounts and percentages: `1234.56`, `1234,56`, `1.234,56` and `1,234.56` all work. With both separators the last
  one is the decimal separator; a comma alone is a decimal comma.
- Countries: ISO 3166 code (`DE`, `PL`) or the English or German name (`Poland`, `Polen`).
- Text with several lines uses `\n`.
- Hive BDC computes all totals (line amounts, VAT, gross, amount due) from the lines. Totals in the JSON
  (`netAmount`, `taxAmount`, `grossAmount`, `amountDue`, `lineNetAmount`, ...) are only used for a plausibility check.
- When a term comes under two keys, the first non-empty key in the file wins.

After each run the Hive BDC log shows what it took: `e-invoice BT-43 Seller contact email address = '...' (from JSON
'senderContactEmail')`, and the keys it did not use: `JSON fields not written to the e-invoice: ...`. Check this line
after a change to the export: a key in that list has a typo or no EN 16931 term.

## What XRechnung requires

Without these the receiver's validator rejects the invoice, and the Hive BDC pre-flight warns:

| Key | BT | Why |
|---|---|---|
| `buyerReference` (or `reference`) | BT-10 | BR-DE-15: buyer reference, the Leitweg-ID for public buyers |
| `senderName`, `senderStreet`, `senderPostcode`, `senderCity`, `senderCountry` | BT-27, 35, 38, 37, 40 | seller and address |
| `senderVatId` or `senderTaxId` | BT-31 / BT-32 | seller tax registration |
| `senderPhone` (or `senderContactPhone`) | BT-42 | BR-DE-6: seller contact phone |
| `senderEmail` (or `senderContactEmail`) | BT-43 | BR-DE-7: seller contact mail |
| `recipientName`, `recipientPostcode`, `recipientCity`, `recipientCountry` | BT-44, 53, 52, 55 | buyer and address |
| `recipientEmail` (or `buyerElectronicAddress`) | BT-49 | PEPPOL-EN16931-R010: buyer electronic address |
| `paymentTermsText` or `dueDate` | BT-20 / BT-9 | BR-CO-25: payment terms or due date when an amount is due |
| `bankIban` or `bankAccounts` | BT-84 | payment account for a bank transfer |
| `deliveryDate` | BT-72 | delivery date (the issue date is used without it) |
| `deliveryCity`, `deliveryPostcode` | BT-77, 78 | BR-DE-10/11: only when a deliver-to address is sent |
| `vatNote` | BT-120 | the exemption reason for VAT category E, AE, K, G or O |

## Core fields

| Key | BT | Notes |
|---|---|---|
| `documentType` | | `SalesInvoice` |
| `documentNumber` / `invoiceNumber` | BT-1 | invoice number, also the file name |
| `eInvoiceType` | | `XRechnung` (XML file), `ZUGFeRD` (PDF with the XML inside), `NoEInvoice` (the PDF only) |
| `invoiceTypeCode` | BT-3 | UNTDID 1001: `380` invoice, `381` credit note, `384` corrected, `875`, `876`, `877` construction invoices; a credit note keeps positive amounts |
| `issueDate` | BT-2 | |
| `dueDate` | BT-9 | |
| `currency` | BT-5 | ISO 4217, default `EUR` |
| `reference` | BT-10 / BT-13 | fills buyer reference and purchase order when no own key is sent |
| `senderId`, `senderGln` | BT-29, BT-29-0 | seller number at the buyer, GLN (13 digits) |
| `recipientId`, `recipientGln` | BT-46, BT-46-0 | buyer number, GLN |
| `recipientVatId` | BT-48 | |
| `vatRate`, `vatCategory` | BT-119, BT-118 | header default for the lines; category UNTDID 5305: `S`, `Z`, `E`, `AE`, `K`, `G`, `O`, `L`, `M` |
| `vatNote` | BT-120 | VAT exemption reason text |
| `notes` | BT-22 | free invoice note |
| `legalNotice` | BT-22 | legal information, written as note with subject `REG` |
| `servicePeriodFrom`, `servicePeriodTo` | BT-134, BT-135 | service period of every line |
| `paymentTermsText` | BT-20 | payment terms text |
| `cashDiscountDate`, `cashDiscountPercent`, `cashDiscountAmount`, `cashDiscountDays` | BT-20 | one cash discount (Skonto); several as the list `cashDiscounts` with `date`, `percent`, `amount`, `days` |
| `bankIban`, `bankSwift`, `bankAccountHolder` | BT-84, 86, 85 | one account; several as `bankAccounts` with the same keys per entry |
| `zrKennzeichen` | BT-12 | settlement house contract number, padded to 4 digits |

### Line fields (each entry of `lineItems`)

| Key | BT | Notes |
|---|---|---|
| `description` | BT-153 | item name |
| `quantity` | BT-129 | |
| `unit` | BT-130 | UN/ECE Rec 20 code (`C62`, `H87`, `HUR`, `KGM`, `SET`, ...) or `piece`, `stk`, `hour`, `kg`, `m`, `l`; anything else becomes `C62` with a warning |
| `unitPrice` | BT-146 | net price per unit |
| `vatRate`, `vatCategory` | BT-152, BT-151 | line VAT, else the header's |
| `priceUnit`, `priceUnitPrice` | BT-149, BT-146 | AX price unit (for example 100) and the price per that quantity |

## All further terms

Every key below is optional. The first spelling is the recommended one; the others are accepted too.

### Document

| Key | BT | Notes |
|---|---|---|
| `buyerReference` (`leitwegId`) | BT-10 | |
| `projectReference`, `projectName` | BT-11 | the name is written only with a reference |
| `contractReference` | BT-12 | |
| `purchOrderReference`, `purchaseOrderReference` (`customerOrderNumber`, `bestellnummer`) | BT-13 | buyer's order number |
| `orderReference` (`salesOrderNumber`, `auftragsnummer`) | BT-14 | seller's order number |
| `receivingAdviceReference` | BT-15 | |
| `deliveryNoteNumber` (`packingSlipNumber`, `despatchAdviceReference`) | BT-16 | |
| `tenderReference` | BT-17 | |
| `invoicedObjectIdentifier`, `invoicedObjectScheme` | BT-18, BT-18-1 | |
| `buyerAccountingReference` (`costCenter`) | BT-19 | |
| `notesSubjectCode` | BT-21 | UNTDID 4451 subject of `notes` (`AAI`, `SUR`, `REG`, ...) |
| `invoiceNote` | BT-22 | one more note |
| `deliveryTerms` (else `deliveryTermsCode`), `deliveryMode` | BT-22 | written as one note: `Delivery terms: DDP, delivered duty paid; Shipping method: UPS` |
| `precedingInvoiceNumber`, `precedingInvoiceDate` | BT-25, BT-26 | the invoice a credit note or correction refers to |
| `taxCurrency`, `taxAmountInTaxCurrency` | BT-6, BT-111 | VAT in a second currency; both or neither |
| `taxPointDate` or `taxPointDateCode` | BT-7 / BT-8 | one of the two |
| `invoicingPeriodStart`, `invoicingPeriodEnd` | BT-73, BT-74 | invoicing period of the whole invoice |
| `roundingAmount` | BT-114 | |
| `prepaidAmount` | BT-113 | amount already paid; the amount due becomes gross minus this |
| `vatExemptionReasonCode` | BT-121 | VATEX code next to `vatNote` |

### Seller

| Key | BT | Notes |
|---|---|---|
| `senderTradingName` | BT-28 | |
| `senderIdScheme` | BT-29-1 | scheme of `senderId` (ISO 6523 ICD) |
| `senderLegalRegistrationId`, `senderLegalRegistrationScheme` | BT-30, BT-30-1 | commercial register number |
| `senderAdditionalLegalInformation` | BT-33 | |
| `senderElectronicAddress`, `senderElectronicAddressScheme` | BT-34, BT-34-1 | default: `senderEmail` with scheme `EM` |
| `senderStreet2`, `senderAddressLine3`, `senderCountrySubdivision` | BT-36, BT-162, BT-39 | |
| `senderContactName` | BT-41 | contact person; default the company name |
| `senderContactDepartment` | BT-41-0 | department instead of a person (see "Contact name or department") |
| `senderContactPhone` | BT-42 | default `senderPhone` |
| `senderContactEmail` | BT-43 | default `senderEmail`; `senderEmail` stays the electronic address BT-34 |

### Buyer

| Key | BT | Notes |
|---|---|---|
| `recipientTradingName` | BT-45 | |
| `recipientIdScheme` | BT-46-1 | |
| `recipientLegalRegistrationId`, `recipientLegalRegistrationScheme` | BT-47, BT-47-1 | |
| `buyerElectronicAddress`, `buyerElectronicAddressScheme` | BT-49, BT-49-1 | default `recipientEmail`; a Leitweg-ID gets scheme `0204` |
| `recipientStreet2`, `recipientAddressLine3`, `recipientCountrySubdivision` | BT-51, BT-163, BT-54 | |
| `recipientContactName` or `recipientContactDepartment` | BT-56 / BT-56-0 | |
| `recipientContactPhone`, `recipientContactEmail` | BT-57, BT-58 | |

### Payee and tax representative

| Key | BT | Notes |
|---|---|---|
| `payeeName` | BT-59 | required for the payee group |
| `payeeIdentifier`, `payeeIdentifierScheme` | BT-60, BT-60-1 | |
| `payeeLegalRegistrationId`, `payeeLegalRegistrationScheme` | BT-61, BT-61-1 | |
| `taxRepresentativeName`, `taxRepresentativeVatId`, `taxRepresentativeCountry` | BT-62, BT-63, BT-69 | all three required for the group |
| `taxRepresentativeStreet`, `taxRepresentativeAddressLine2`, `taxRepresentativeAddressLine3` | BT-64, 65, 164 | |
| `taxRepresentativeCity`, `taxRepresentativePostcode`, `taxRepresentativeCountrySubdivision` | BT-66, 67, 68 | |

### Delivery

| Key | BT | Notes |
|---|---|---|
| `deliveryName` | BT-70 | |
| `deliveryLocationId`, `deliveryLocationScheme` or `deliveryGln` | BT-71, BT-71-1 | |
| `deliveryDate` | BT-72 | |
| `deliveryStreet` | BT-75 | several lines with `\n`: line 2 and 3 become BT-76 and BT-165 |
| `deliveryStreet2`, `deliveryAddressLine3` | BT-76, BT-165 | override the lines of `deliveryStreet` |
| `deliveryCity`, `deliveryPostcode`, `deliveryCountrySubdivision`, `deliveryCountry` | BT-77, 78, 79, 80 | country defaults to the buyer's |

### Payment

| Key | BT | Notes |
|---|---|---|
| `paymentMeansCode` | BT-81 | UNTDID 4461; default `58` (SEPA transfer, valid IBAN) or `30`, `59` with a direct debit, `48` with a card |
| `paymentMeansText` | BT-82 | |
| `remittanceInformation` (`paymentReference`) | BT-83 | |
| `paymentCardNumber`, `paymentCardHolder` | BT-87, BT-88 | send only the last digits of the card number |
| `mandateReference`, `creditorIdentifier`, `debitedIban` | BT-89, BT-90, BT-91 | SEPA direct debit; all three required |

### Document level allowances and charges

| Key | BT | Notes |
|---|---|---|
| `chargeTotalAmount`, `chargeReason` | BT-99, BT-104 | sum of the header charges (freight, ...); negative on an invoice = allowance |
| `documentChargeBaseAmount`, `documentChargePercentage`, `documentChargeReasonCode` | BT-100, 101, 105 | |
| `documentChargeVatCategory`, `documentChargeVatRate` | BT-102, BT-103 | default the header VAT |
| `documentAllowanceAmount`, `documentAllowanceReason`, `documentAllowanceReasonCode` | BT-92, 97, 98 | header discount |
| `documentAllowanceBaseAmount`, `documentAllowancePercentage` | BT-93, BT-94 | the amount is computed from them when it is missing |
| `documentAllowanceVatCategory`, `documentAllowanceVatRate` | BT-95, BT-96 | default the header VAT |

### Supporting documents

| Key | BT | Notes |
|---|---|---|
| `supportingDocumentReference` | BT-122 | required |
| `supportingDocumentDescription` | BT-123 | |
| `supportingDocumentUrl` | BT-124 | |
| `attachmentContent` (base64) or `attachmentFileName` | BT-125, BT-125-2 | a file name must be a file in the same folder as the JSON, without a path |
| `attachmentMimeCode` | BT-125-1 | `application/pdf`, `image/png`, `image/jpeg`, `text/csv`, xlsx, ods; derived from the file extension when missing; at most 10 MB |

### Line terms

| Key | BT | Notes |
|---|---|---|
| `lineNote` (`note`) | BT-127 | |
| `objectIdentifier`, `objectIdentifierScheme` | BT-128, BT-128-1 | serial number or similar |
| `orderLineReference` | BT-132 | buyer's order line |
| `lineAccountingReference` (`costCenter`) | BT-133 | |
| `linePeriodStart`, `linePeriodEnd` | BT-134, BT-135 | overrides `servicePeriodFrom` / `servicePeriodTo` for this line |
| `lineDiscountAmount`, `lineDiscountReason`, `lineDiscountReasonCode` | BT-136, 139, 140 | |
| `lineDiscountBaseAmount`, `lineDiscountPercent` | BT-137, BT-138 | |
| `lineChargeAmount`, `lineChargeReason`, `lineChargeReasonCode` | BT-141, 144, 145 | |
| `lineChargeBaseAmount`, `lineChargePercent` | BT-142, BT-143 | |
| `itemGrossPrice`, `itemPriceDiscount` | BT-148, BT-147 | the discount needs the gross price |
| `priceBaseQuantityUnit` | BT-150 | |
| `itemNote` (`itemDescription`, `longDescription`) | BT-154 | item description |
| `sellerItemId` (`itemId`, `itemNumber`) | BT-155 | |
| `customerItemId` (`buyerItemId`) | BT-156 | |
| `gtin` (`ean`), `gtinScheme` | BT-157, BT-157-1 | scheme default `0160` |
| `tariffCode` (`hsCode`, `zolltarifnummer`) | BT-158 | customs tariff number, written with scheme `HS`, spaces removed |
| `itemClassificationCode`, `itemClassificationScheme`, `itemClassificationSchemeVersion` | BT-158, 158-1, 158-2 | any other classification; the scheme is required |
| `originCountry` | BT-159 | |
| `itemAttributes` | BT-160, BT-161 | text, one `name: value` per line, split at the first colon |
| `batchNumber` (`chargennummer`, `lotNumber`) | BT-160, BT-161 | attribute with the name `Charge` |
| `itemAttributeName`, `itemAttributeValue` | BT-160, BT-161 | one attribute |

## Lists

Groups that can repeat may also come as a JSON list of objects:

```json
"invoiceNotes": [ { "text": "Second note", "subjectCode": "SUR" } ],
"precedingInvoices": [ { "number": "RE-2026-0100", "issueDate": "2026-10-01" } ],
"allowancesCharges": [
  { "chargeAmount": "7.50", "reason": "Packaging", "reasonCode": "ABL" },
  { "allowanceAmount": "10.00", "reason": "Discount", "vatCategory": "S", "vatRate": "19" },
  { "baseAmount": "200.00", "percentage": "5", "isCharge": "false", "reasonCode": "95" }
],
"supportingDocuments": [
  { "reference": "DN-1", "description": "Delivery note", "url": "https://example.com/dn-1" },
  { "reference": "DRW-1", "fileName": "drawing.pdf" }
],
"lineItems": [ {
  "description": "Valve", "quantity": "2", "unit": "piece", "unitPrice": "100.00",
  "itemClassifications": [ { "code": "84818000", "scheme": "HS" }, { "code": "12345", "scheme": "TST", "schemeVersion": "1" } ],
  "itemAttributes": [ { "name": "Colour", "value": "Blue" }, { "name": "Material", "value": "1.4404" } ]
} ]
```

The list entries also take the BT numbers as keys. In `allowancesCharges` the key `chargeAmount` or `allowanceAmount`
says what the entry is; with a plain `amount`, `isCharge` decides, else a positive amount is a charge.

## Rules worth knowing

- **A group is written whole or not at all.** A payee without name, a tax representative without VAT id or country, a
  card without number, a direct debit without one of its three terms, an attribute without name or value and a
  classification without scheme are left out with a warning in the log, so the invoice still passes the validator.
- **Contact name or department.** XRechnung allows a person name or a department in a contact, not both (CII-SR-465,
  CII-SR-466). Hive BDC writes the department only when no contact name is sent; for the seller the department then
  replaces the company name that is the default contact.
- **Seller mail.** `senderEmail` is the seller's electronic address (BT-34). Send the person's mail as
  `senderContactEmail` (BT-43); without it the contact gets `senderEmail` as well.
- **Credit notes.** Send `invoiceTypeCode` `381`. Negative quantities from the ERP are written as positive amounts,
  the sign lives in the type code.
- **Units.** A unit Hive BDC does not know becomes `C62` (one) with a warning. Send the UN/ECE code where the ERP has
  its own unit names (`SET` for a set, `KGM` for kilogram).

## Complete example

```json
{
  "documentType": "SalesInvoice",
  "eInvoiceType": "ZUGFeRD",
  "invoiceNumber": "RG-100001",
  "invoiceTypeCode": "380",
  "issueDate": "2026-11-25",
  "dueDate": "2027-01-25",
  "currency": "EUR",
  "buyerReference": "4400000001",
  "orderReference": "AF-000001",
  "senderName": "Example Supplier GmbH",
  "senderStreet": "Werkstr. 1",
  "senderPostcode": "10115",
  "senderCity": "Berlin",
  "senderCountry": "DE",
  "senderVatId": "DE123456789",
  "senderEmail": "invoice@example.com",
  "senderPhone": "+49 30 000000",
  "senderContactName": "Muster, Erika",
  "senderContactEmail": "erika.muster@example.com",
  "recipientName": "Example Customer Sp. z o.o.",
  "recipientStreet": "Ul. Przykladowa 5",
  "recipientPostcode": "00-001",
  "recipientCity": "Warszawa",
  "recipientCountry": "PL",
  "recipientVatId": "PL1234567890",
  "recipientEmail": "invoices@example.org",
  "deliveryName": "Example Customer Sp. z o.o.",
  "deliveryStreet": "Ul. Przykladowa 5\nWarehouse 2",
  "deliveryPostcode": "00-001",
  "deliveryCity": "Warszawa",
  "deliveryCountry": "PL",
  "deliveryDate": "2026-11-24",
  "deliveryTerms": "DDP, delivered duty paid, incl. packaging",
  "deliveryMode": "UPS",
  "vatRate": "0",
  "vatCategory": "K",
  "vatNote": "Steuerfreie innergemeinschaftliche Lieferung (§ 4 Nr. 1b, § 6a UStG)",
  "bankIban": "DE89370400440532013000",
  "bankSwift": "COBADEFFXXX",
  "lineItems": [
    {
      "description": "Complete nozzle",
      "quantity": "100",
      "unit": "SET",
      "unitPrice": "1.56",
      "vatRate": "0",
      "vatCategory": "K",
      "sellerItemId": "9000000001",
      "itemNote": "Nozzle, complete, PEEK",
      "originCountry": "DE",
      "tariffCode": "39269097",
      "batchNumber": "PA-000001",
      "itemAttributes": "Material code: PEEK\nDrawing-No. / Index / Date: 000F0000 / 01 / 08.07.2014"
    }
  ]
}
```

This gives, among others: the item description from `itemNote`, the classification `<ram:ClassCode listID="HS">39269097</ram:ClassCode>`,
three `ApplicableProductCharacteristic` entries (Material code, Drawing-No. / Index / Date, Charge), the note
`Delivery terms: DDP, delivered duty paid, incl. packaging; Shipping method: UPS` and Erika Muster's mail in the
seller contact.