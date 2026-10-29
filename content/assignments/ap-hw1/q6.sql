SELECT VendorContactLName + ', ' + VendorContactFName AS [Full Name]
FROM Vendors
WHERE VendorContactLName LIKE '[ABCE]%'
ORDER BY VendorContactLName, VendorContactFName;
