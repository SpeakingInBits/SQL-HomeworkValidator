SELECT InvoiceTotal, InvoiceTotal * 0.10 AS [10%], InvoiceTotal + 10 AS [Plus 10%]
FROM Invoices
WHERE InvoiceTotal - PaymentTotal - CreditTotal > 1000
ORDER BY InvoiceTotal DESC;
