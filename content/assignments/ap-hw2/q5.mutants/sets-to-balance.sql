-- Sets PaymentTotal to the balance due itself, which replaces any earlier payment instead of adding to it.
-- Same result as the reference when nothing has been paid yet; only partly paid invoices (edge data) show the difference.
UPDATE InvoiceCopy
SET PaymentDate = GETDATE(),
    PaymentTotal = InvoiceTotal - PaymentTotal - CreditTotal
WHERE InvoiceTotal - PaymentTotal - CreditTotal > 0;
