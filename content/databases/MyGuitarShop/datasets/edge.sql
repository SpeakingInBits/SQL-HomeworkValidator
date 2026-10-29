-- MyGuitarShop edge dataset: the course data plus rows designed to catch common mistakes.
-- Each block says what it's for. Identity values continue from the base data.

:r base.sql

INSERT INTO Products (CategoryID, ProductCode, ProductName, Description, ListPrice, DiscountPercent, DateAdded)
VALUES
    -- Priced exactly 500 and 2000: "greater than 500 and less than 2000" excludes them.
    (1, 'edge_500', 'Edge Guitar 500', 'Priced exactly 500.', 500.00, 20.00, '2016-08-01 10:00:00'),
    (2, 'edge_2000', 'Edge Bass 2000', 'Priced exactly 2000.', 2000.00, 10.00, '2016-08-02 10:00:00'),
    -- No category: an INNER JOIN to Categories drops it.
    (NULL, 'edge_nocat', 'Edge Accessory', 'Has no category.', 49.99, 0.00, '2016-08-03 10:00:00'),
    -- No date added, and a fractional discount percent.
    (1, 'edge_nodt', 'Edge Guitar No Date', 'Has no date added.', 899.00, 12.50, NULL);

INSERT INTO Customers (EmailAddress, Password, FirstName, LastName)
VALUES
    -- Last name exactly "M": "LastName > 'M'" misses it.
    ('ann.m@example.com', 'edge', 'Ann', 'M'),
    -- A shared last name, inserted with the later first name first.
    ('zed.nguyen@example.com', 'edge', 'Zed', 'Nguyen'),
    ('amy.nguyen@example.com', 'edge', 'Amy', 'Nguyen'),
    -- An apostrophe in the name.
    ('liam.obrien@example.com', 'edge', 'Liam', 'O''Brien');
GO

-- An order placed one second before midnight on New Year's Eve, with an item whose
-- ItemTotal is exactly 500: (300 - 50) * 2. "ItemTotal > 500" excludes it.
INSERT INTO Orders (CustomerID, OrderDate, ShipAmount, TaxAmount, ShipDate, ShipAddressID,
                    CardType, CardNumber, CardExpires, BillingAddressID)
VALUES (1, '2016-12-31 23:59:59', 5.00, 32.50, '2017-01-03 09:00:00', 1,
        'Visa', '4111111111111111', '04/2018', 2);

DECLARE @order int = SCOPE_IDENTITY();

INSERT INTO OrderItems (OrderID, ProductID, ItemPrice, DiscountAmount, Quantity)
VALUES (@order, 8, 300.00, 50.00, 2),
       (@order, 1, 699.00, 209.70, 3);

-- An order placed exactly at midnight that hasn't shipped.
INSERT INTO Orders (CustomerID, OrderDate, ShipAmount, TaxAmount, ShipDate, ShipAddressID,
                    CardType, CardNumber, CardExpires, BillingAddressID)
VALUES (2, '2017-01-01 00:00:00', 5.00, 10.00, NULL, 3,
        'Visa', '4012888888881881', '08/2020', 3);
GO
