CREATE TABLE [dbo].[PRS_Famille] (
    [Id]         INT            IDENTITY (1, 1) NOT NULL,
    [Libelle]    NVARCHAR (100) NOT NULL,
    [CouleurHex] CHAR (7)       NOT NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC)
);

