CREATE TABLE [dbo].[ChecklistElementModeles] (
    [Id]                INT            IDENTITY (1, 1) NOT NULL,
    [ChecklistModeleId] INT            NOT NULL,
    [Categorie]         NVARCHAR (100) NOT NULL,
    [SousCategorie]     NVARCHAR (100) NULL,
    [Libelle]           NVARCHAR (255) NOT NULL,
    [Obligatoire]       BIT            DEFAULT ((0)) NOT NULL,
    [Priorite]          INT            DEFAULT ((3)) NOT NULL,
    [DelaiDefautJours]  INT            NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC),
    FOREIGN KEY ([ChecklistModeleId]) REFERENCES [dbo].[ChecklistModeles] ([Id]) ON DELETE CASCADE
);

