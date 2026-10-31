SELECT AccountDescription, COUNT(*) AS LineItemCount, SUM(InvoiceLineItemAmount) AS LineItemSum
FROM GLAccounts g
    JOIN InvoiceLineItems li ON g.AccountNo = li.AccountNo
    JOIN Invoices i ON li.InvoiceID = i.InvoiceID
WHERE InvoiceDate BETWEEN '2022-12-01' AND '2023-02-28'
GROUP BY AccountDescription
HAVING COUNT(*) > 1
ORDER BY LineItemCount DESC;
