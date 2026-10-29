IF OBJECT_ID('VendorCopy') IS NOT NULL
    DROP TABLE VendorCopy;
IF OBJECT_ID('InvoiceCopy') IS NOT NULL
    DROP TABLE InvoiceCopy;

SELECT *
INTO VendorCopy
FROM Vendors;

SELECT *
INTO InvoiceCopy
FROM Invoices;
