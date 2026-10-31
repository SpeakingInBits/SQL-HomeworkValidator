SELECT *
FROM Vendors
    LEFT JOIN Invoices ON Vendors.VendorID = Invoices.VendorID;
