SELECT EmailAddress,
       SUM(ItemPrice) AS ItemPriceTotal,
       SUM(DiscountAmount) AS DiscountAmountTotal
FROM Customers c
    JOIN Orders o ON c.CustomerID = o.CustomerID
    JOIN OrderItems oi ON o.OrderID = oi.OrderID
GROUP BY EmailAddress
ORDER BY ItemPriceTotal DESC;
