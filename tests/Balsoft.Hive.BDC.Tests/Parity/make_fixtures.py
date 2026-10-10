#!/usr/bin/env python3
"""Writes the synthetic AX export fixtures for the e-invoice parity test (Parity/Fixtures/*.json).
Each fixture exercises one branch of InvoiceMapper. Run from the test project folder."""
import copy
import json
import pathlib

BASE = {
    "documentType": "SalesInvoice", "documentNumber": "2026000001", "party": "Customer",
    "eInvoiceType": "XRechnung", "issueDate": "2026-09-15", "dueDate": "2026-10-15", "currency": "EUR",
    "reference": "PO-4711", "senderName": "Muster Lieferant GmbH & Co. KG", "senderStreet": "Industriestr. 10",
    "senderPostcode": "50667", "senderCity": "Köln", "senderCountry": "DE", "senderVatId": "DE987654321",
    "senderEmail": "finance@muster-lieferant.de", "senderPhone": "+49 221 123456-0",
    "recipientName": "Muster Kunde GmbH", "recipientStreet": "Musterstr. 1", "recipientPostcode": "10115",
    "recipientCity": "Berlin", "recipientCountry": "DE", "recipientVatId": "DE123456789",
    "recipientEmail": "ap@muster-kunde.de", "environment": "Test", "dataAreaId": "001", "vatRate": "19",
    "bankIban": "DE89 3704 0044 0532 0130 00",
    "lineItems": [
        {"description": "Türschließer Typ 5000", "quantity": "4", "unit": "Stk", "unitPrice": "89.90"},
        {"description": "Montagezubehör", "quantity": "1", "unit": "Stk", "unitPrice": "12.50"},
    ],
}


def variant(**changes):
    d = copy.deepcopy(BASE)
    for k, v in changes.items():
        if v is None:
            d.pop(k, None)
        else:
            d[k] = v
    return d


FIXTURES = {
    "01-plain": variant(),
    "02-aktivbank": variant(zrKennzeichen="126", senderId="88636", senderGln="4067107000001", recipientId="10038",
                            recipientGln="4012345000009", invoiceTypeCode="380"),
    "03-aktivbank-bad-gln": variant(zrKennzeichen="0126", senderId="88636", recipientGln="11111111111"),
    "04-skonto-flat": variant(paymentTermsText="Zahlbar bis 15.10.2026 ohne Abzug.", cashDiscountDate="2026-09-25",
                              cashDiscountPercent="2", cashDiscountAmount="9.44"),
    "05-skonto-list": variant(cashDiscounts=[{"date": "2026-09-22", "percent": "3"}, {"days": 20, "percent": "2", "amount": "7.50"}]),
    "06-credit-negative-qty": variant(documentNumber="2026100001", lineItems=[
        {"description": "Retoure Typ 5000", "quantity": "-2", "unit": "Stk", "unitPrice": "89.90"}]),
    "07-credit-code-381": variant(documentNumber="2026100002", invoiceTypeCode="381", lineItems=[
        {"description": "Gutschrift Preisdifferenz", "quantity": "1", "unit": "piece", "unitPrice": "25.00"}]),
    "08-corrected-384": variant(invoiceTypeCode="384"),
    "09-bonus-84": variant(invoiceTypeCode="84"),
    "10-mixed-vat": variant(lineItems=[
        {"description": "Beschlag", "quantity": "2", "unit": "Stk", "unitPrice": "100.00", "vatRate": "19"},
        {"description": "Fachbuch", "quantity": "1", "unit": "Stk", "unitPrice": "30.00", "vatRate": "7"}]),
    "11-exempt-note": variant(vatRate="0", vatNote="Steuerfreie innergemeinschaftliche Lieferung"),
    "12-reverse-charge": variant(lineItems=[
        {"description": "Bauleistung", "quantity": "1", "unit": "qty", "unitPrice": "5000", "vatRate": "0", "vatCategory": "AE"}],
        vatNote="Steuerschuldnerschaft des Leistungsempfängers"),
    "13-service-period": variant(servicePeriodFrom="2026-08-01", servicePeriodTo="2026-08-31", notes="Wartung August"),
    "14-no-iban-no-reference": variant(bankIban=None, reference=None),
    "15-no-email-phone": variant(senderEmail=None, senderPhone=None, recipientEmail=None),
    "16-negative-price-discount": variant(lineItems=[
        {"description": "Ware", "quantity": "3", "unit": "Stk", "unitPrice": "50.00"},
        {"description": "Rabatt", "quantity": "1", "unit": "Stk", "unitPrice": "-15.00"}]),
    "17-units-countries": variant(senderCountry="Germany", recipientCountry="Österreich", recipientVatId="U12345678",
                                  lineItems=[
                                      {"description": "Stunden", "quantity": "2.5", "unit": "h", "unitPrice": "95"},
                                      {"description": "Kabel", "quantity": "12", "unit": "m", "unitPrice": "1.99"},
                                      {"description": "Unbekannt", "quantity": "1", "unit": "Palette", "unitPrice": "10"},
                                      {"description": "Rec20", "quantity": "1", "unit": "SET", "unitPrice": "20"}]),
    "18-pre20-invoice-number": variant(documentType=None, documentNumber=None, invoiceNumber="2026000018"),
    "19-zugferd": variant(eInvoiceType="ZUGFeRD"),
    "20-rounding": variant(lineItems=[
        {"description": "Kleinteil", "quantity": "3", "unit": "Stk", "unitPrice": "0.333"},
        {"description": "Kleinteil 2", "quantity": "7", "unit": "Stk", "unitPrice": "1.005"}]),
}

if __name__ == "__main__":
    out = pathlib.Path(__file__).parent / "Fixtures"
    out.mkdir(exist_ok=True)
    for name, data in FIXTURES.items():
        (out / f"{name}.json").write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{len(FIXTURES)} fixtures written to {out}")
