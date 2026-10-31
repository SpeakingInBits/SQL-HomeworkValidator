SELECT VendorID, COUNT(PaymentTotal) AS PaymentSum
FROM Invoices
GROUP BY VendorID;
