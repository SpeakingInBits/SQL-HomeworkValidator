SELECT VendorID, InvoiceDate, InvoiceTotal,
       SUM(InvoiceTotal) OVER (PARTITION BY InvoiceDate) AS VendorTotal,
       COUNT(InvoiceTotal) OVER (PARTITION BY InvoiceDate) AS VendorCount,
       AVG(InvoiceTotal) OVER (PARTITION BY InvoiceDate) AS VendorAvg
FROM Invoices;
