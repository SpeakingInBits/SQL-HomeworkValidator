-- Deletes vendors that haven't sent an invoice, not vendors in states that haven't.
DELETE VendorCopy
WHERE VendorID NOT IN (SELECT VendorID FROM InvoiceCopy);
