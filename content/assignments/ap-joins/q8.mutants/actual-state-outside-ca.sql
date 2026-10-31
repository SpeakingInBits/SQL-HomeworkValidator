SELECT VendorName, 'CA' AS VendorState
FROM Vendors
WHERE VendorState = 'CA'
UNION
SELECT VendorName, VendorState
FROM Vendors
WHERE VendorState <> 'CA'
ORDER BY VendorName;
