SELECT CategoryName, ProductName, ListPrice
FROM Categories c
    JOIN Products p ON c.CategoryID = p.CategoryID
ORDER BY ProductName;
