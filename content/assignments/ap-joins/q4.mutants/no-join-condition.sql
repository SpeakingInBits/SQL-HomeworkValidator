SELECT VendorName, InvoiceNumber, InvoiceDate,
       InvoiceTotal - (PaymentTotal + CreditTotal) AS Balance
FROM Vendors v, Invoices i
WHERE InvoiceTotal - (PaymentTotal + CreditTotal) <> 0
ORDER BY VendorName;
