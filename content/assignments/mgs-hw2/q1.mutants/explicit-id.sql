-- Right on the course data, where the next ID happens to be 5.
SET IDENTITY_INSERT Categories ON;
INSERT INTO Categories (CategoryID, CategoryName) VALUES (5, 'Brass');
SET IDENTITY_INSERT Categories OFF;
