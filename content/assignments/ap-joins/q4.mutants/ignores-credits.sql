SELECT VendorName, InvoiceNumber, InvoiceDate,
       InvoiceTotal - PaymentTotal AS Balance
FROM Vendors v, Invoices i
WHERE v.VendorID = i.VendorID
  AND InvoiceTotal - PaymentTotal <> 0
ORDER BY VendorName;
