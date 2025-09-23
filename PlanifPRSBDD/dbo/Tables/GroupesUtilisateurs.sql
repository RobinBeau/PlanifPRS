CREATE TABLE [dbo].[GroupesUtilisateurs] (
    [Id]           INT            IDENTITY (1, 1) NOT NULL,
    [NomGroupe]    NVARCHAR (100) NOT NULL,
    [Description]  NVARCHAR (500) NULL,
    [DateCreation] DATETIME       DEFAULT (getdate()) NULL,
    [CreePar]      NVARCHAR (100) NULL,
    [Actif]        BIT            DEFAULT ((1)) NOT NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC)
);

