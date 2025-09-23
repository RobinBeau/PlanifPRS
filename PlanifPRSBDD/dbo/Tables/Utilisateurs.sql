CREATE TABLE [dbo].[Utilisateurs] (
    [Id]           INT            IDENTITY (1, 1) NOT NULL,
    [Matricule]    NVARCHAR (20)  NOT NULL,
    [Nom]          NVARCHAR (100) NOT NULL,
    [Prenom]       NVARCHAR (100) NOT NULL,
    [LoginWindows] NVARCHAR (100) NOT NULL,
    [Mail]         NVARCHAR (150) NULL,
    [Service]      NVARCHAR (100) NULL,
    [DateImport]   DATETIME       DEFAULT (getdate()) NULL,
    [DateDeleted]  DATETIME       NULL,
    [Droits]       NVARCHAR (50)  NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC)
);

