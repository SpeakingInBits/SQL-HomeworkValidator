SELECT c.CategoryName, p.ProductID
FROM Categories c
    LEFT JOIN Products p ON c.CategoryID = p.CategoryID
WHERE c.CategoryID IS NULL;
