-- Fails: VendorCopy's VendorID is an identity column, so it can't be given a value without a column list.
INSERT INTO VendorCopy
SELECT * FROM Vendors WHERE VendorState <> 'CA';
