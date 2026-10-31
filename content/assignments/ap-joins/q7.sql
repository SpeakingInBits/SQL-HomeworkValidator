SELECT g.AccountNo, g.AccountDescription
FROM GLAccounts g
    LEFT JOIN InvoiceLineItems li ON g.AccountNo = li.AccountNo
WHERE li.AccountNo IS NULL
ORDER BY g.AccountNo;
