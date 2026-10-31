SELECT AccountDescription, COUNT(*) AS LineItemCount, SUM(InvoiceLineItemAmount) AS LineItemSum
FROM GLAccounts g
    JOIN InvoiceLineItems li ON g.AccountNo = li.AccountNo
GROUP BY AccountDescription
HAVING COUNT(*) > 1
ORDER BY LineItemCount;
