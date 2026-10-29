SELECT VendorContactLName + ', ' + VendorContactFName AS [Full Name]
FROM Vendors
WHERE VendorContactLName LIKE '[A-C]%'
ORDER BY VendorContactLName, VendorContactFName;
