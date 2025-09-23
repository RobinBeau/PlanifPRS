CREATE TABLE [dbo].[Secteur] (
    [id]            INT            IDENTITY (1, 1) NOT NULL,
    [idTypeSecteur] INT            NOT NULL,
    [nom]           NVARCHAR (255) NOT NULL,
    [description]   NVARCHAR (MAX) NULL,
    [activation]    BIT            DEFAULT ((1)) NOT NULL,
    [doNotDelete]   BIT            DEFAULT ((0)) NOT NULL,
    [dateCreated]   DATETIME       DEFAULT (getdate()) NOT NULL,
    [dateModified]  DATETIME       NULL,
    [dateDeleted]   DATETIME       NULL,
    PRIMARY KEY CLUSTERED ([id] ASC)
);

