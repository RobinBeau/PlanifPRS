CREATE TABLE [dbo].[PRS_Checklist] (
    [Id]                      INT            IDENTITY (1, 1) NOT NULL,
    [PRSId]                   INT            NULL,
    [Tache]                   NVARCHAR (200) NULL,
    [Statut]                  BIT            NULL,
    [Commentaire]             NVARCHAR (500) NULL,
    [Categorie]               NVARCHAR (100) NULL,
    [SousCategorie]           NVARCHAR (100) NULL,
    [Libelle]                 NVARCHAR (255) NULL,
    [Obligatoire]             BIT            DEFAULT ((0)) NULL,
    [EstCoche]                BIT            DEFAULT ((0)) NULL,
    [DateValidation]          DATETIME       NULL,
    [ValidePar]               NVARCHAR (100) NULL,
    [CreatedByLogin]          NVARCHAR (100) NULL,
    [DateCreation]            DATETIME       DEFAULT (getdate()) NULL,
    [ChecklistModeleSourceId] INT            NULL,
    [PrsSourceId]             INT            NULL,
    [FamilleId]               INT            NULL,
    [Priorite]                INT            DEFAULT ((3)) NOT NULL,
    [DateEcheance]            DATETIME2 (7)  NULL,
    [DelaiDefautJours]        INT            NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [CK_PRS_Checklist_Priorite] CHECK ([Priorite]>=(1) AND [Priorite]<=(5)),
    FOREIGN KEY ([PRSId]) REFERENCES [dbo].[PRS] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_PRS_Checklist_ChecklistModeles] FOREIGN KEY ([ChecklistModeleSourceId]) REFERENCES [dbo].[ChecklistModeles] ([Id]),
    CONSTRAINT [FK_PRS_Checklist_Famille] FOREIGN KEY ([FamilleId]) REFERENCES [dbo].[PRS_Famille] ([Id]) ON DELETE SET NULL,
    CONSTRAINT [FK_PRS_Checklist_PRS_Source] FOREIGN KEY ([PrsSourceId]) REFERENCES [dbo].[PRS] ([Id]),
    CONSTRAINT [FK_PRS_Checklist_PrsSource] FOREIGN KEY ([PrsSourceId]) REFERENCES [dbo].[PRS] ([Id])
);


GO
CREATE NONCLUSTERED INDEX [IX_PRS_Checklist_DateEcheance]
    ON [dbo].[PRS_Checklist]([DateEcheance] ASC) WHERE ([DateEcheance] IS NOT NULL);


GO
CREATE NONCLUSTERED INDEX [IX_PRS_Checklist_Categorie]
    ON [dbo].[PRS_Checklist]([Categorie] ASC) WHERE ([Categorie] IS NOT NULL);

