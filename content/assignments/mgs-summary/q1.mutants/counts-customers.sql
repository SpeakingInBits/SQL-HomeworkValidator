SELECT COUNT(DISTINCT CustomerID) AS OrderCount, SUM(TaxAmount) AS TaxTotal
FROM Orders;
