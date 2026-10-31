SELECT CategoryName, ProductName, ListPrice
FROM Categories c
    RIGHT JOIN Products p ON c.CategoryID = p.CategoryID
ORDER BY CategoryName, ProductName;
