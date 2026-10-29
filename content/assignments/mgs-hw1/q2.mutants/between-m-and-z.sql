SELECT LastName + ', ' + FirstName AS FullName
FROM Customers
WHERE LastName BETWEEN 'M' AND 'Z'
ORDER BY LastName;
