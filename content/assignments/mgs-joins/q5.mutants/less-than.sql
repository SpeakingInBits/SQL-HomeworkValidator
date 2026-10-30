SELECT DISTINCT p1.ProductName, p1.ListPrice
FROM Products p1
    JOIN Products p2 ON p1.ProductID < p2.ProductID
                    AND p1.ListPrice = p2.ListPrice
ORDER BY p1.ProductName;
