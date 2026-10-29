CREATE TABLE Individuals
(
    IndividualID  int           NOT NULL  IDENTITY PRIMARY KEY,
    FirstName     varchar(50)   NOT NULL,
    LastName      varchar(50)   NOT NULL,
    Address       varchar(100)  NULL,
    Phone         varchar(20)   NULL
);

CREATE TABLE Groups
(
    GroupID    int          NOT NULL  IDENTITY PRIMARY KEY,
    GroupName  varchar(50)  NOT NULL,
    Dues       money        NOT NULL  DEFAULT 0  CHECK (Dues >= 0)
);

CREATE TABLE GroupMembership
(
    GroupID       int  NOT NULL  REFERENCES Groups (GroupID),
    IndividualID  int  NOT NULL  REFERENCES Individuals (IndividualID)
);
