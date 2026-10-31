SELECT VendorName, COUNT(DISTINCT i.InvoiceID) AS AccountCount
FROM Vendors v
    JOIN Invoices i ON v.VendorID = i.VendorID
    JOIN InvoiceLineItems li ON i.InvoiceID = li.InvoiceID
GROUP BY VendorName
HAVING COUNT(DISTINCT i.InvoiceID) > 1;
