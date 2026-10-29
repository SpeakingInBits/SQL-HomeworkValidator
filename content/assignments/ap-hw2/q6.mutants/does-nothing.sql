-- Right on the course data, where those invoices already have TermsID 2.
UPDATE InvoiceCopy
SET TermsID = 2
WHERE TermsID = 2 AND VendorID IN (SELECT VendorID FROM VendorCopy);
