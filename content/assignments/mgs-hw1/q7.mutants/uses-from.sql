SELECT TOP 1 100 AS Price, .07 AS TaxRate, 100 * .07 AS TaxAmount, 100 + (100 * .07) AS Total
FROM Products;
