SELECT VendorName, SUM(PaymentTotal) AS PaymentSum
FROM Vendors v
    JOIN Invoices i ON v.VendorID = i.VendorID
GROUP BY VendorName
ORDER BY PaymentSum DESC;
