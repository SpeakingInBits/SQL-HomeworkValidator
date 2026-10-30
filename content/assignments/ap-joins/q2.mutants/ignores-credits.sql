SELECT VendorName, InvoiceNumber, InvoiceDate,
       InvoiceTotal - PaymentTotal AS Balance
FROM Vendors v
    JOIN Invoices i ON v.VendorID = i.VendorID
WHERE InvoiceTotal - PaymentTotal <> 0
ORDER BY VendorName;
