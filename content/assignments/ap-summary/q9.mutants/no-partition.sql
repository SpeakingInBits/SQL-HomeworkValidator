SELECT VendorID, InvoiceDate, InvoiceTotal,
       SUM(InvoiceTotal) OVER () AS VendorTotal,
       COUNT(InvoiceTotal) OVER () AS VendorCount,
       AVG(InvoiceTotal) OVER () AS VendorAvg
FROM Invoices;
