SELECT AccountNo, SUM(InvoiceTotal) AS InvoiceSum
FROM InvoiceLineItems li
    JOIN Invoices i ON li.InvoiceID = i.InvoiceID
GROUP BY AccountNo WITH ROLLUP;
