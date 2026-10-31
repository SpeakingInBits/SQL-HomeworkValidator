SELECT AccountDescription, COUNT(*) AS LineItemCount, SUM(InvoiceLineItemAmount) AS LineItemSum
FROM GLAccounts g
    JOIN InvoiceLineItems li ON g.AccountNo = li.AccountNo
GROUP BY AccountDescription
ORDER BY LineItemCount DESC;
