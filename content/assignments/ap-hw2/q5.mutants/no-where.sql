UPDATE InvoiceCopy
SET PaymentDate = GETDATE(),
    PaymentTotal = InvoiceTotal - PaymentTotal - CreditTotal;
