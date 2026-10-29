UPDATE InvoiceCopy
SET TermsID = 2
FROM InvoiceCopy JOIN VendorCopy ON InvoiceCopy.VendorID = VendorCopy.VendorID
WHERE InvoiceCopy.TermsID = 2;
