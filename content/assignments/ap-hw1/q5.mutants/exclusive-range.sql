SELECT InvoiceNumber AS Number, InvoiceTotal AS Total,
       PaymentTotal + CreditTotal AS Credits,
       InvoiceTotal - (PaymentTotal + CreditTotal) AS Balance
FROM Invoices
WHERE InvoiceTotal > 500 AND InvoiceTotal < 10000;
