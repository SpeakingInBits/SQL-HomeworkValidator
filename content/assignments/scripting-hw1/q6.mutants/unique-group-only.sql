-- Each group could then have only one member.
DROP TABLE GroupMembership;
CREATE TABLE GroupMembership
(
    GroupID int NOT NULL UNIQUE REFERENCES Groups (GroupID),
    IndividualID int NOT NULL REFERENCES Individuals (IndividualID)
);
