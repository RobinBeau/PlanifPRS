CREATE TABLE [dbo].[JalonUtilisateurs] (
    [JalonId]       INT NOT NULL,
    [UtilisateurId] INT NOT NULL,
    PRIMARY KEY CLUSTERED ([JalonId] ASC, [UtilisateurId] ASC),
    FOREIGN KEY ([JalonId]) REFERENCES [dbo].[PRS_Jalons] ([Id]),
    FOREIGN KEY ([UtilisateurId]) REFERENCES [dbo].[Utilisateurs] ([Id])
);

