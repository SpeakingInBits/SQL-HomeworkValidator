-- MyGuitarShop schema: tables, keys and defaults.
-- Taken from original.sql without the database-level statements; the validator runs this inside its own database.

CREATE TABLE Categories (
  CategoryID        INT            PRIMARY KEY   IDENTITY,
  CategoryName      VARCHAR(255)   NOT NULL      UNIQUE
);

CREATE TABLE Products (
  ProductID         INT            PRIMARY KEY   IDENTITY,
  CategoryID        INT            REFERENCES Categories (CategoryID), 
  ProductCode       VARCHAR(10)    NOT NULL      UNIQUE,
  ProductName       VARCHAR(255)   NOT NULL,
  Description       TEXT           NOT NULL,
  ListPrice         MONEY          NOT NULL,
  DiscountPercent   MONEY          NOT NULL      DEFAULT 0.00,
  DateAdded         DATETIME                     DEFAULT NULL
);

CREATE TABLE Customers (
  CustomerID           INT            PRIMARY KEY   IDENTITY,
  EmailAddress         VARCHAR(255)   NOT NULL      UNIQUE,
  Password             VARCHAR(60)    NOT NULL,
  FirstName            VARCHAR(60)    NOT NULL,
  LastName             VARCHAR(60)    NOT NULL,
  ShippingAddressID    INT                          DEFAULT NULL,
  BillingAddressID     INT                          DEFAULT NULL
);

CREATE TABLE Addresses (
  AddressID          INT            PRIMARY KEY   IDENTITY,
  CustomerID         INT            REFERENCES Customers (CustomerID),
  Line1              VARCHAR(60)    NOT NULL,
  Line2              VARCHAR(60)                  DEFAULT NULL,
  City               VARCHAR(40)    NOT NULL,
  State              VARCHAR(2)     NOT NULL,
  ZipCode            VARCHAR(10)    NOT NULL,
  Phone              VARCHAR(12)    NOT NULL,
  Disabled           INT            NOT NULL      DEFAULT 0
);

CREATE TABLE Orders (
  OrderID           INT            PRIMARY KEY  IDENTITY,
  CustomerID        INT            REFERENCES Customers (CustomerID),
  OrderDate         DATETIME       NOT NULL,
  ShipAmount        MONEY          NOT NULL,
  TaxAmount         MONEY          NOT NULL,
  ShipDate          DATETIME                    DEFAULT NULL,
  ShipAddressID     INT            NOT NULL,
  CardType          VARCHAR(50)    NOT NULL,
  CardNumber        CHAR(16)       NOT NULL,
  CardExpires       CHAR(7)        NOT NULL,
  BillingAddressID  INT            NOT NULL    
);

CREATE TABLE OrderItems (
  ItemID             INT            PRIMARY KEY  IDENTITY,
  OrderID            INT            REFERENCES Orders (OrderID),
  ProductID          INT            REFERENCES Products (ProductID),
  ItemPrice          MONEY  NOT NULL,
  DiscountAmount     MONEY  NOT NULL,
  Quantity           INT            NOT NULL    
);

CREATE TABLE Administrators (
  AdminID           INT            PRIMARY KEY   IDENTITY,
  EmailAddress      VARCHAR(255)   NOT NULL,
  Password          VARCHAR(255)   NOT NULL,
  FirstName         VARCHAR(255)   NOT NULL,
  LastName          VARCHAR(255)   NOT NULL
);
