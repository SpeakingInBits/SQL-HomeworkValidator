-- The other reading: record a payment of the balance due, so PaymentTotal becomes what's been paid in total
-- and nothing is left owing. The two readings only differ for partly paid invoices.
UPDATE InvoiceCopy
SET PaymentDate = GETDATE(),
    PaymentTotal = InvoiceTotal - CreditTotal
WHERE InvoiceTotal - PaymentTotal - CreditTotal > 0;
