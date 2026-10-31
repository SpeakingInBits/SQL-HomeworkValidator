SELECT VendorName, DefaultAccountNo, AccountDescription
FROM Vendors v
    JOIN GLAccounts g ON v.DefaultAccountNo = g.AccountNo
ORDER BY AccountDescription;
