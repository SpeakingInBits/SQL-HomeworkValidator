SELECT *
FROM Invoices
WHERE (InvoiceTotal - PaymentTotal - CreditTotal > 0 AND PaymentDate IS NOT NULL)
   OR (InvoiceTotal - PaymentTotal - CreditTotal <= 0 AND PaymentDate IS NULL);
