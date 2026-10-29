INSERT INTO VendorCopy
    (VendorName, VendorAddress1, VendorAddress2, VendorCity, VendorState, VendorZipCode,
     VendorPhone, VendorContactLName, VendorContactFName, DefaultTermsID, DefaultAccountNo)
SELECT VendorName, VendorAddress1, VendorAddress2, VendorCity, VendorState, VendorZipCode,
       VendorPhone, VendorContactLName, VendorContactFName, DefaultTermsID, DefaultAccountNo
FROM Vendors
WHERE VendorState = 'CA';
