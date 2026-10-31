SELECT CategoryName, COUNT(*) AS ProductCount, MIN(ListPrice) AS MostExpensiveProduct
FROM Categories c
    JOIN Products p ON c.CategoryID = p.CategoryID
GROUP BY CategoryName
ORDER BY ProductCount DESC;
