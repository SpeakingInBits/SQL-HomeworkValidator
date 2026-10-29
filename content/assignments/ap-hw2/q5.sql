-- Pays each invoice off: PaymentTotal becomes everything paid so far plus the balance due,
-- which is InvoiceTotal - CreditTotal, so nothing is left owing.
UPDATE InvoiceCopy
SET PaymentDate = GETDATE(),
    PaymentTotal = InvoiceTotal - CreditTotal
WHERE InvoiceTotal - PaymentTotal - CreditTotal > 0;
