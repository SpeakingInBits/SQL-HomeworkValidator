SELECT ProductName, SUM((ListPrice - DiscountAmount) * Quantity) AS ProductTotal
FROM Products p
    JOIN OrderItems oi ON p.ProductID = oi.ProductID
GROUP BY ProductName WITH ROLLUP;
