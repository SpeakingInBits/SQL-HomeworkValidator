-- Fails: CategoryName is UNIQUE, so every category can't be renamed Woodwinds.
UPDATE Categories
SET CategoryName = 'Woodwinds';
