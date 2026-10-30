-- Allan Sherwood is customer 1 in the course data.
SELECT FirstName, LastName, Line1, City, State, ZipCode
FROM Customers c
    JOIN Addresses a ON c.CustomerID = a.CustomerID
WHERE c.CustomerID = 1;
