-- AP edge dataset: the course data plus rows designed to catch common mistakes.
-- Each block says what it's for. Identity values continue from the base data.

:r base.sql

-- Terms that no invoice uses: an INNER JOIN drops them, a LEFT JOIN keeps them.
INSERT Terms (TermsDescription, TermsDueDays) VALUES ('Net due 120 days', 120);

-- Two contacts who share a last name, inserted with the later first name first,
-- so a query sorted by last name alone returns them in the wrong order.
-- Also NULL address line 2 and phone.
INSERT Vendors (VendorName, VendorAddress1, VendorAddress2, VendorCity, VendorState, VendorZipCode,
                VendorPhone, VendorContactLName, VendorContactFName, DefaultTermsID, DefaultAccountNo)
VALUES ('Edge Case Supply', '100 Boundary Way', NULL, 'Fresno', 'CA', '93711',
        NULL, 'Ellis', 'Zoe', 3, 570),
       ('Edge Case Printing', '200 Boundary Way', 'Suite 5', 'Fresno', 'CA', '93711',
        '(559) 555-0100', 'Ellis', 'Adam', 3, 570);
GO

DECLARE @vendor int = (SELECT VendorID FROM Vendors WHERE VendorName = 'Edge Case Supply');

INSERT Invoices (VendorID, InvoiceNumber, InvoiceDate, InvoiceTotal, PaymentTotal, CreditTotal,
                 TermsID, InvoiceDueDate, PaymentDate)
VALUES
    -- Totals exactly on the edges of 500 to 10,000: "> 500 AND < 10000" misses them.
    (@vendor, 'EDGE-500',      '2023-02-01',   500.00,   500.00,  0.00, 3, '2023-03-03', '2023-02-20'),
    (@vendor, 'EDGE-10000',    '2023-02-02', 10000.00, 10000.00,  0.00, 3, '2023-03-04', '2023-02-21'),
    -- Totals just outside those edges.
    (@vendor, 'EDGE-499.99',   '2023-02-03',   499.99,   499.99,  0.00, 3, '2023-03-05', '2023-02-22'),
    (@vendor, 'EDGE-10000.01', '2023-02-04', 10000.01, 10000.01,  0.00, 3, '2023-03-06', '2023-02-23'),
    -- Balance due of exactly 1,000: "greater than 1000" excludes it.
    (@vendor, 'EDGE-BAL-1000', '2023-02-05',  1500.00,   500.00,  0.00, 3, '2023-03-07', NULL),
    -- Paid entirely by a credit: no balance due, so the payment date is valid. Catches balances that ignore CreditTotal.
    (@vendor, 'EDGE-CREDIT',   '2023-02-06',    75.00,     0.00, 75.00, 3, '2023-03-08', '2023-02-15'),
    -- Invalid: balance due, but it has a payment date.
    (@vendor, 'EDGE-BAD-DATE', '2023-02-07',   250.00,   100.00,  0.00, 3, '2023-03-09', '2023-02-28'),
    -- Invalid: nothing due, but no payment date.
    (@vendor, 'EDGE-NO-DATE',  '2023-02-08',   300.00,   300.00,  0.00, 3, '2023-03-10', NULL);
GO

-- A vendor whose default terms are 2, with an invoice on different terms (3). In the course data every
-- invoice from such a vendor already has TermsID 2, so "set TermsID to 2 for invoices from vendors with
-- DefaultTermsID 2" changes nothing there; this row makes a do-nothing UPDATE fail.
INSERT Vendors (VendorName, VendorAddress1, VendorAddress2, VendorCity, VendorState, VendorZipCode,
                VendorPhone, VendorContactLName, VendorContactFName, DefaultTermsID, DefaultAccountNo)
VALUES ('Edge Terms Supply', '300 Boundary Way', NULL, 'Fresno', 'CA', '93711',
        '(559) 555-0101', 'Ortega', 'Luis', 2, 570);
GO

DECLARE @termsVendor int = (SELECT VendorID FROM Vendors WHERE VendorName = 'Edge Terms Supply');

INSERT Invoices (VendorID, InvoiceNumber, InvoiceDate, InvoiceTotal, PaymentTotal, CreditTotal,
                 TermsID, InvoiceDueDate, PaymentDate)
VALUES (@termsVendor, 'EDGE-TERMS', '2023-02-09', 820.00, 820.00, 0.00, 3, '2023-03-11', '2023-02-25');
GO

-- A third contact named Robert: a self-join on first name then pairs each Robert with two others,
-- so it returns each of them twice unless the query uses DISTINCT.
INSERT Vendors (VendorName, VendorAddress1, VendorAddress2, VendorCity, VendorState, VendorZipCode,
                VendorPhone, VendorContactLName, VendorContactFName, DefaultTermsID, DefaultAccountNo)
VALUES ('Edge Contact Supply', '400 Boundary Way', NULL, 'Fresno', 'CA', '93711',
        '(559) 555-0102', 'Edgerton', 'Robert', 3, 570);

-- An account no invoice line item uses, so a hardcoded list of the course data's unused accounts misses it.
INSERT GLAccounts (AccountNo, AccountDescription) VALUES (999, 'Edge Unused Account');
GO

-- Freight invoices with line items on February 28, 2023 and the day after, so a filter for "December 1, 2022
-- to February 28, 2023" that leaves out the last day, or runs into March, counts the wrong line items.
DECLARE @rangeVendor int = (SELECT VendorID FROM Vendors WHERE VendorName = 'Edge Case Supply');

INSERT Invoices (VendorID, InvoiceNumber, InvoiceDate, InvoiceTotal, PaymentTotal, CreditTotal,
                 TermsID, InvoiceDueDate, PaymentDate)
VALUES (@rangeVendor, 'EDGE-FEB-28', '2023-02-28', 120.00, 120.00, 0.00, 3, '2023-03-30', '2023-03-15'),
       (@rangeVendor, 'EDGE-MAR-1',  '2023-03-01', 130.00, 130.00, 0.00, 3, '2023-03-31', '2023-03-16');

INSERT InvoiceLineItems (InvoiceID, InvoiceSequence, AccountNo, InvoiceLineItemAmount, InvoiceLineItemDescription)
SELECT InvoiceID, 1, 553, InvoiceTotal, 'Edge freight'
FROM Invoices
WHERE InvoiceNumber IN ('EDGE-FEB-28', 'EDGE-MAR-1');
GO
