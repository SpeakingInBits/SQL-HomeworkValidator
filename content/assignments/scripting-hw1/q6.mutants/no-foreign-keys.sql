DROP TABLE GroupMembership;
CREATE TABLE GroupMembership
(
    GroupID int NOT NULL,
    IndividualID int NOT NULL,
    UNIQUE (GroupID, IndividualID)
);
