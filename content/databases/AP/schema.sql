-- AP schema: tables, keys, defaults and foreign keys.
-- Taken from original.sql without the database-level statements; the validator runs this inside its own database.
-- Foreign keys are created normally here instead of WITH NOCHECK, so data scripts must insert parents before children.

-- create the ContactUpdates table
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE ContactUpdates(
	VendorID  int IDENTITY(1,1) NOT NULL,
	LastName  varchar(50) NULL,
	FirstName varchar(50) NULL
)
GO

-- create the GLAccounts table
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE GLAccounts (
	AccountNo int NOT NULL,
	AccountDescription varchar(50) NOT NULL,
    CONSTRAINT PK_GLAccounts PRIMARY KEY CLUSTERED 
    (
	    AccountNo ASC
    ) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON)
) 
GO

-- create the InvoiceArchive table
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE InvoiceArchive (
	InvoiceID int NOT NULL,
	VendorID int NOT NULL,
	InvoiceNumber varchar(50) NOT NULL,
	InvoiceDate date NOT NULL,
	InvoiceTotal money NOT NULL,
	PaymentTotal money NOT NULL,
	CreditTotal money NOT NULL,
	TermsID int NOT NULL,
	InvoiceDueDate date NOT NULL,
	PaymentDate date NULL
) 
GO

-- create the InvoiceLineItems table
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE InvoiceLineItems(
	InvoiceID int NOT NULL,
	InvoiceSequence smallint NOT NULL,
	AccountNo int NOT NULL,
	InvoiceLineItemAmount money NOT NULL,
	InvoiceLineItemDescription varchar(100) NOT NULL,
    CONSTRAINT PK_InvoiceLineItems PRIMARY KEY CLUSTERED 
    (
	    InvoiceID ASC,
	    InvoiceSequence ASC
    ) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON)
) 
GO

-- create the Invoices table
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE Invoices (
	InvoiceID int IDENTITY(1,1) NOT NULL,
	VendorID int NOT NULL,
	InvoiceNumber varchar(50) NOT NULL,
	InvoiceDate date NOT NULL,
	InvoiceTotal money NOT NULL,
	PaymentTotal money NOT NULL,
	CreditTotal money NOT NULL,
	TermsID int NOT NULL,
	InvoiceDueDate date NOT NULL,
	PaymentDate date NULL,
    CONSTRAINT PK_Invoices PRIMARY KEY CLUSTERED 
    (
	    InvoiceID ASC
    ) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) 
GO

-- create the Terms table
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE Terms (
	TermsID int IDENTITY(1,1) NOT NULL,
	TermsDescription varchar(50) NOT NULL,
	TermsDueDays smallint NOT NULL,
    CONSTRAINT PK_Terms PRIMARY KEY CLUSTERED 
    (
	    TermsID ASC
    ) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) 
) ON [PRIMARY]
GO

-- create the Vendors table
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE Vendors (
	VendorID int IDENTITY(1,1) NOT NULL,
	VendorName varchar(50) NOT NULL,
	VendorAddress1 varchar(50) NULL,
	VendorAddress2 varchar(50) NULL,
	VendorCity varchar(50) NOT NULL,
	VendorState char(2) NOT NULL,
	VendorZipCode varchar(20) NOT NULL,
	VendorPhone varchar(50) NULL,
	VendorContactLName varchar(50) NULL,
	VendorContactFName varchar(50) NULL,
	DefaultTermsID int NOT NULL,
	DefaultAccountNo int NOT NULL,
    CONSTRAINT PK_Vendors PRIMARY KEY CLUSTERED 
    (
	    VendorID ASC
    ) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON)
)
GO

ALTER TABLE Invoices ADD  CONSTRAINT DF_Invoices_PaymentTotal  DEFAULT ((0)) FOR PaymentTotal
GO
ALTER TABLE Invoices ADD  CONSTRAINT DF_Invoices_CreditTotal  DEFAULT ((0)) FOR CreditTotal
GO
ALTER TABLE Vendors ADD  CONSTRAINT DF_Vendors_TermsID  DEFAULT ((3)) FOR DefaultTermsID
GO
ALTER TABLE Vendors ADD  CONSTRAINT DF_Vendors_AccountNo  DEFAULT ((570)) FOR DefaultAccountNo
GO
ALTER TABLE InvoiceLineItems ADD CONSTRAINT FK_InvoiceLineItems_GLAccounts FOREIGN KEY(AccountNo)
REFERENCES GLAccounts (AccountNo)
GO
ALTER TABLE InvoiceLineItems ADD CONSTRAINT FK_InvoiceLineItems_Invoices FOREIGN KEY(InvoiceID)
REFERENCES Invoices (InvoiceID)
ON DELETE CASCADE
GO
ALTER TABLE Invoices ADD CONSTRAINT FK_Invoices_Terms FOREIGN KEY(TermsID)
REFERENCES Terms (TermsID)
GO
ALTER TABLE Invoices ADD CONSTRAINT FK_Invoices_Vendors FOREIGN KEY(VendorID)
REFERENCES Vendors (VendorID)
GO
ALTER TABLE Vendors ADD CONSTRAINT FK_Vendors_GLAccounts FOREIGN KEY(DefaultAccountNo)
REFERENCES GLAccounts (AccountNo)
GO
ALTER TABLE Vendors ADD CONSTRAINT FK_Vendors_Terms FOREIGN KEY(DefaultTermsID)
REFERENCES Terms (TermsID)
GO
