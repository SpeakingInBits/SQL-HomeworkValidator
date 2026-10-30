SELECT FirstName, LastName, Line1, City, State, ZipCode
FROM Customers c
    JOIN Addresses a ON c.ShippingAddressID = a.AddressID;
