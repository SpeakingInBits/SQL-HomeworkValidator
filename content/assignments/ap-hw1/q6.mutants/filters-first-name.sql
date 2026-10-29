SELECT VendorContactLName + ', ' + VendorContactFName AS [Full Name]
FROM Vendors
WHERE VendorContactFName LIKE '[ABCE]%'
ORDER BY VendorContactLName, VendorContactFName;
