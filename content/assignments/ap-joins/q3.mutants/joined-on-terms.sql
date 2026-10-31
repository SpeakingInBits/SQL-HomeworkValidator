SELECT VendorName, DefaultAccountNo, AccountDescription
FROM Vendors v
    JOIN GLAccounts g ON v.DefaultTermsID = g.AccountNo
ORDER BY AccountDescription, VendorName;
