SELECT c.CategoryName, p.ProductID
FROM Categories c
    JOIN Products p ON c.CategoryID = p.CategoryID
WHERE p.ProductID IS NULL;
