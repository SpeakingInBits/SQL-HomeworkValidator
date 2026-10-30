SELECT VendorName, COUNT(li.AccountNo) AS AccountCount
FROM Vendors v
    JOIN Invoices i ON v.VendorID = i.VendorID
    JOIN InvoiceLineItems li ON i.InvoiceID = li.InvoiceID
GROUP BY VendorName
HAVING COUNT(li.AccountNo) > 1;
