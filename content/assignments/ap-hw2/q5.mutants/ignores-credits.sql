UPDATE InvoiceCopy
SET PaymentDate = GETDATE(),
    PaymentTotal = InvoiceTotal - PaymentTotal
WHERE InvoiceTotal - PaymentTotal > 0;
