SELECT ProductName, ListPrice, DiscountPercent,
       ListPrice * DiscountPercent AS DiscountAmount,
       ListPrice - ListPrice * DiscountPercent AS DiscountPrice
FROM Products
ORDER BY DiscountPrice DESC;
