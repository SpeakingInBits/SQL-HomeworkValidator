ALTER TABLE Invoices WITH NOCHECK
ADD CHECK (((PaymentDate IS NULL AND PaymentTotal = 0) OR (PaymentDate IS NOT NULL AND PaymentTotal > 0))
           AND PaymentTotal + CreditTotal <= InvoiceTotal);
