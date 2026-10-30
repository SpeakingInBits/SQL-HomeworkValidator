SELECT VendorName, 'Outside CA' AS VendorState
FROM Vendors
WHERE VendorState = 'CA'
UNION
SELECT VendorName, 'CA'
FROM Vendors
WHERE VendorState <> 'CA'
ORDER BY VendorName;
