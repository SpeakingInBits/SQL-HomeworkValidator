SELECT Vendors.*
FROM Vendors
    JOIN Invoices ON Vendors.VendorID = Invoices.VendorID;
