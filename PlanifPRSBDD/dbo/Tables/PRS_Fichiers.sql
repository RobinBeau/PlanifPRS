CREATE TABLE [dbo].[PRS_Fichiers] (
    [Id]             INT            IDENTITY (1, 1) NOT NULL,
    [PrsId]          INT            NOT NULL,
    [NomOriginal]    NVARCHAR (255) NOT NULL,
    [CheminFichier]  NVARCHAR (500) NOT NULL,
    [TypeMime]       NVARCHAR (100) NULL,
    [Taille]         BIGINT         NOT NULL,
    [DateUpload]     DATETIME2 (7)  DEFAULT (getdate()) NOT NULL,
    [UploadParLogin] NVARCHAR (100) NULL,
    CONSTRAINT [PK_PRS_Fichiers] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_PRS_Fichiers_PRS] FOREIGN KEY ([PrsId]) REFERENCES [dbo].[PRS] ([Id]) ON DELETE CASCADE
);


GO
CREATE NONCLUSTERED INDEX [IX_PRS_Fichiers_PrsId]
    ON [dbo].[PRS_Fichiers]([PrsId] ASC);

