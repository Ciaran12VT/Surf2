-- One immutable table-data revision owns one JSON array through surf.DataSet.
-- Writing datasets append to the array transactionally until publication.
CREATE TABLE surf.FullTableData (
    DataSetKey bigint NOT NULL CONSTRAINT PK_FullTableData PRIMARY KEY,
    JsonPayload nvarchar(max) NOT NULL,
    CONSTRAINT FK_FullTableData_DataSet FOREIGN KEY (DataSetKey) REFERENCES surf.DataSet(DataSetKey));
