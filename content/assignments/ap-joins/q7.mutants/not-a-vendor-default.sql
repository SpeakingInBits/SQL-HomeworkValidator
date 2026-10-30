SELECT g.AccountNo, g.AccountDescription
FROM GLAccounts g
    LEFT JOIN Vendors v ON g.AccountNo = v.DefaultAccountNo
WHERE v.VendorID IS NULL
ORDER BY g.AccountNo;
