SELECT CategoryName, COUNT(p.ProductID) AS ProductCount, MAX(ListPrice) AS MostExpensiveProduct
FROM Categories c
    LEFT JOIN Products p ON c.CategoryID = p.CategoryID
GROUP BY CategoryName
ORDER BY ProductCount DESC;
