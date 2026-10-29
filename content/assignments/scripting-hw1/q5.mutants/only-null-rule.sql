-- Only half of the first rule: a payment without a date still gets through.
ALTER TABLE Invoices WITH NOCHECK
ADD CHECK (PaymentDate IS NOT NULL OR PaymentTotal = 0),
    CHECK ((PaymentTotal + CreditTotal) <= InvoiceTotal);
