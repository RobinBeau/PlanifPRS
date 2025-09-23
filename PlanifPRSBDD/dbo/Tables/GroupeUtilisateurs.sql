CREATE TABLE [dbo].[GroupeUtilisateurs] (
    [GroupeId]      INT      NOT NULL,
    [UtilisateurId] INT      NOT NULL,
    [DateAjout]     DATETIME DEFAULT (getdate()) NULL,
    PRIMARY KEY CLUSTERED ([GroupeId] ASC, [UtilisateurId] ASC),
    FOREIGN KEY ([GroupeId]) REFERENCES [dbo].[GroupesUtilisateurs] ([Id]),
    FOREIGN KEY ([UtilisateurId]) REFERENCES [dbo].[Utilisateurs] ([Id])
);

