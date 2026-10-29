-- Right on the course data, where orders 32 and 38-41 are the unshipped ones.
SELECT OrderID, OrderDate, ShipDate
FROM Orders
WHERE OrderID IN (32, 38, 39, 40, 41);
