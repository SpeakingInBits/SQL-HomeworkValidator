DELETE VendorCopy
WHERE VendorState IN
    (SELECT DISTINCT VendorState
     FROM VendorCopy JOIN InvoiceCopy ON VendorCopy.VendorID = InvoiceCopy.VendorID);
