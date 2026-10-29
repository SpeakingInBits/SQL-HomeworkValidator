SELECT *
FROM Invoices
WHERE (InvoiceTotal - PaymentTotal > 0 AND PaymentDate IS NOT NULL)
   OR (InvoiceTotal - PaymentTotal <= 0 AND PaymentDate IS NULL);
