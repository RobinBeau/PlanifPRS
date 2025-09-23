CREATE TABLE [dbo].[PRS_LiensDossierPRS] (
    [Id]             INT            IDENTITY (1, 1) NOT NULL,
    [PrsId]          INT            NOT NULL,
    [Chemin]         NVARCHAR (500) NOT NULL,
    [Description]    NVARCHAR (500) NULL,
    [DateAjout]      DATETIME       DEFAULT (getdate()) NOT NULL,
    [AjouteParLogin] NVARCHAR (100) NOT NULL,
    CONSTRAINT [PK_PRS_LiensDossierPRS] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_PRS_LiensDossierPRS_PRS] FOREIGN KEY ([PrsId]) REFERENCES [dbo].[PRS] ([Id]) ON DELETE CASCADE
);


GO
CREATE NONCLUSTERED INDEX [IX_PRS_LiensDossierPRS_PrsId]
    ON [dbo].[PRS_LiensDossierPRS]([PrsId] ASC);

