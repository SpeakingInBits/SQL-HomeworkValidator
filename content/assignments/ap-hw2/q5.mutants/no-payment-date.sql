UPDATE InvoiceCopy
SET PaymentTotal = InvoiceTotal - PaymentTotal - CreditTotal
WHERE InvoiceTotal - PaymentTotal - CreditTotal > 0;
