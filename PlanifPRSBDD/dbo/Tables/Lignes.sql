CREATE TABLE [dbo].[Lignes] (
    [Id]           INT            IDENTITY (1, 1) NOT NULL,
    [Nom]          NVARCHAR (100) NOT NULL,
    [idSecteur]    INT            NULL,
    [description]  VARCHAR (100)  NULL,
    [activation]   BIT            NULL,
    [doNotDelete]  BIT            NULL,
    [dateCreated]  DATETIME       NULL,
    [dateModified] DATETIME       NULL,
    [dateDeleted]  DATETIME       NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC)
);

