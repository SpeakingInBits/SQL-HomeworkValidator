SELECT v.VendorName AS Vendor, i.InvoiceDate AS Date, i.InvoiceNumber AS Number,
       li.InvoiceSequence AS [#], li.InvoiceLineItemAmount AS LineItem
FROM Vendors v
    JOIN Invoices i ON v.VendorID = i.VendorID
    JOIN InvoiceLineItems li ON v.VendorID = li.InvoiceID
ORDER BY Vendor, Date, Number, [#];
