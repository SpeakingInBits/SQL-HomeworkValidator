SELECT InvoiceNumber Number,
       InvoiceTotal Total,
       PaymentTotal + CreditTotal Credits,
       InvoiceTotal - (PaymentTotal + CreditTotal) Balance
FROM Invoices;
