-- Keyboards is the unused category in the course data.
SELECT c.CategoryName, p.ProductID
FROM Categories c
    LEFT JOIN Products p ON c.CategoryID = p.CategoryID
WHERE c.CategoryName = 'Keyboards';
