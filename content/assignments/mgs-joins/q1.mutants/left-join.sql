SELECT CategoryName, ProductName, ListPrice
FROM Categories c
    LEFT JOIN Products p ON c.CategoryID = p.CategoryID
ORDER BY CategoryName, ProductName;
