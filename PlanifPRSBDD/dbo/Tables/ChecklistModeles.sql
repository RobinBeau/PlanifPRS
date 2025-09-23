CREATE TABLE [dbo].[ChecklistModeles] (
    [Id]                INT            IDENTITY (1, 1) NOT NULL,
    [Nom]               NVARCHAR (100) NOT NULL,
    [Description]       NVARCHAR (500) NULL,
    [FamilleEquipement] NVARCHAR (100) NULL,
    [DateCreation]      DATETIME       DEFAULT (getdate()) NOT NULL,
    [CreatedByLogin]    NVARCHAR (100) NOT NULL,
    [Actif]             BIT            DEFAULT ((1)) NOT NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC)
);

