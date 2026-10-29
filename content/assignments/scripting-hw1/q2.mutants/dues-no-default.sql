CREATE TABLE Individuals
(
    IndividualID int IDENTITY PRIMARY KEY, FirstName varchar(50), LastName varchar(50), Address varchar(100), Phone varchar(20)
);
CREATE TABLE Groups
(
    GroupID int IDENTITY PRIMARY KEY, GroupName varchar(50), Dues money NOT NULL CHECK (Dues >= 0)
);
CREATE TABLE GroupMembership
(
    GroupID int REFERENCES Groups (GroupID), IndividualID int REFERENCES Individuals (IndividualID)
);
