CREATE TABLE [dbo].[ChecklistAffectations] (
    [Id]              INT            IDENTITY (1, 1) NOT NULL,
    [ChecklistId]     INT            NOT NULL,
    [UtilisateurId]   INT            NULL,
    [GroupeId]        INT            NULL,
    [TypeAffectation] NVARCHAR (20)  NOT NULL,
    [DateAffectation] DATETIME       DEFAULT (getdate()) NOT NULL,
    [AffectePar]      NVARCHAR (100) NOT NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [CK_ChecklistAffectations_UserOrGroup] CHECK ([UtilisateurId] IS NOT NULL AND [GroupeId] IS NULL OR [UtilisateurId] IS NULL AND [GroupeId] IS NOT NULL),
    FOREIGN KEY ([ChecklistId]) REFERENCES [dbo].[PRS_Checklist] ([Id]),
    FOREIGN KEY ([GroupeId]) REFERENCES [dbo].[GroupesUtilisateurs] ([Id]),
    FOREIGN KEY ([UtilisateurId]) REFERENCES [dbo].[Utilisateurs] ([Id])
);

