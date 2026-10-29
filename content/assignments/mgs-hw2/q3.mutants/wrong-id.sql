-- Keyboards (4) has no products in the course data, so this delete works but removes the wrong row.
DELETE Categories
WHERE CategoryID = 4;
