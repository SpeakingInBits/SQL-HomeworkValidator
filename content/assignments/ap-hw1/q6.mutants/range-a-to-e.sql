SELECT VendorContactLName + ', ' + VendorContactFName AS [Full Name]
FROM Vendors
WHERE VendorContactLName LIKE '[A-E]%'
ORDER BY VendorContactLName, VendorContactFName;
