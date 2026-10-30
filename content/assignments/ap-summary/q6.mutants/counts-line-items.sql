SELECT AccountNo, COUNT(*) AS LineItemCount
FROM InvoiceLineItems
GROUP BY AccountNo WITH ROLLUP;
