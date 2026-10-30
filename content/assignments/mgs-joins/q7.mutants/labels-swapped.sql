SELECT 'SHIPPED' AS ShipStatus, OrderID, OrderDate
FROM Orders
WHERE ShipDate IS NULL
UNION
SELECT 'NOT SHIPPED', OrderID, OrderDate
FROM Orders
WHERE ShipDate IS NOT NULL
ORDER BY OrderDate;
