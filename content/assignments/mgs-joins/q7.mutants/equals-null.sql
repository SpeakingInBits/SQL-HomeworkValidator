SELECT 'SHIPPED' AS ShipStatus, OrderID, OrderDate
FROM Orders
WHERE ShipDate IS NOT NULL
UNION
SELECT 'NOT SHIPPED', OrderID, OrderDate
FROM Orders
WHERE ShipDate = NULL
ORDER BY OrderDate;
