CREATE TABLE [dbo].[PrsAffectations] (
    [Id]              INT            IDENTITY (1, 1) NOT NULL,
    [PrsId]           INT            NOT NULL,
    [UtilisateurId]   INT            NULL,
    [GroupeId]        INT            NULL,
    [TypeAffectation] NVARCHAR (50)  NOT NULL,
    [DateAffectation] DATETIME       DEFAULT (getdate()) NULL,
    [AffectePar]      NVARCHAR (100) NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC),
    CHECK ([UtilisateurId] IS NOT NULL OR [GroupeId] IS NOT NULL),
    CHECK ([UtilisateurId] IS NULL OR [GroupeId] IS NULL),
    FOREIGN KEY ([GroupeId]) REFERENCES [dbo].[GroupesUtilisateurs] ([Id]),
    FOREIGN KEY ([PrsId]) REFERENCES [dbo].[PRS] ([Id]),
    FOREIGN KEY ([UtilisateurId]) REFERENCES [dbo].[Utilisateurs] ([Id])
);

