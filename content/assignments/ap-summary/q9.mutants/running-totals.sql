SELECT VendorID, InvoiceDate, InvoiceTotal,
       SUM(InvoiceTotal) OVER (PARTITION BY VendorID ORDER BY InvoiceDate) AS VendorTotal,
       COUNT(InvoiceTotal) OVER (PARTITION BY VendorID ORDER BY InvoiceDate) AS VendorCount,
       AVG(InvoiceTotal) OVER (PARTITION BY VendorID ORDER BY InvoiceDate) AS VendorAvg
FROM Invoices;
