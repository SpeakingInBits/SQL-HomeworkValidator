CREATE TABLE GroupMembership
(
    GroupID int NOT NULL REFERENCES Groups (GroupID),
    IndividualID int NOT NULL REFERENCES Individuals (IndividualID),
    UNIQUE (GroupID, IndividualID)
);
