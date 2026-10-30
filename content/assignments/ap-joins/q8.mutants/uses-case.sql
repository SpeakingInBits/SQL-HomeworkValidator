SELECT VendorName,
       CASE WHEN VendorState = 'CA' THEN 'CA' ELSE 'Outside CA' END AS VendorState
FROM Vendors
ORDER BY VendorName;
