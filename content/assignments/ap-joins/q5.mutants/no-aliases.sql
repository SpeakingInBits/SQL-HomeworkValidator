SELECT v.VendorName, i.InvoiceDate, i.InvoiceNumber, li.InvoiceSequence, li.InvoiceLineItemAmount
FROM Vendors v
    JOIN Invoices i ON v.VendorID = i.VendorID
    JOIN InvoiceLineItems li ON i.InvoiceID = li.InvoiceID
ORDER BY VendorName, InvoiceDate, InvoiceNumber, InvoiceSequence;
