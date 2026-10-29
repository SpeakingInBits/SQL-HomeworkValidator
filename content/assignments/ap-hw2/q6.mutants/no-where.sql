UPDATE InvoiceCopy
SET TermsID = 2
WHERE VendorID IN (SELECT VendorID FROM VendorCopy);
