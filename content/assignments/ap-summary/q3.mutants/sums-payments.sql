SELECT VendorName, COUNT(*) AS InvoiceCount, SUM(PaymentTotal) AS InvoiceSum
FROM Vendors v
    JOIN Invoices i ON v.VendorID = i.VendorID
GROUP BY VendorName
ORDER BY InvoiceCount DESC;
