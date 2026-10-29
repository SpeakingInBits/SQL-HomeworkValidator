SELECT InvoiceNumber AS Number, InvoiceTotal AS Total,
       PaymentTotal + CreditTotal AS Credits,
       InvoiceTotal - (PaymentTotal + CreditTotal) AS Balance
FROM Invoices
WHERE InvoiceTotal - (PaymentTotal + CreditTotal) BETWEEN 500 AND 10000;
