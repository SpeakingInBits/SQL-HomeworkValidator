SELECT EmailAddress, COUNT(DISTINCT o.OrderID) AS ProductCount
FROM Customers c
    JOIN Orders o ON c.CustomerID = o.CustomerID
    JOIN OrderItems oi ON o.OrderID = oi.OrderID
GROUP BY EmailAddress
HAVING COUNT(DISTINCT o.OrderID) > 1;
