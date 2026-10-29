-- Fails: Password is NOT NULL.
INSERT INTO Customers (EmailAddress, Password, FirstName, LastName)
VALUES ('rick@raven.com', NULL, 'Rick', 'Raven');
