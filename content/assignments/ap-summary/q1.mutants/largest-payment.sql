SELECT VendorID, MAX(PaymentTotal) AS PaymentSum
FROM Invoices
GROUP BY VendorID;
