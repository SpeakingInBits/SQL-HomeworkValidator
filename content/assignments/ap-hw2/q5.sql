-- "Change PaymentTotal to the balance due", read literally. q5.alternatives/ accepts the other reading.
UPDATE InvoiceCopy
SET PaymentDate = GETDATE(),
    PaymentTotal = InvoiceTotal - PaymentTotal - CreditTotal
WHERE InvoiceTotal - PaymentTotal - CreditTotal > 0;
