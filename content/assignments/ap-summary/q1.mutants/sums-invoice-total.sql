SELECT VendorID, SUM(InvoiceTotal) AS PaymentSum
FROM Invoices
GROUP BY VendorID;
