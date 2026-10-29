DROP TABLE IF EXISTS VendorCopy;
DROP TABLE IF EXISTS InvoiceCopy;
SELECT * INTO VendorCopy FROM Vendors WHERE VendorState = 'CA';
SELECT * INTO InvoiceCopy FROM Invoices;
