CREATE TABLE [dbo].[HistoriqueEdit] (
    [Id]            INT            IDENTITY (1, 1) NOT NULL,
    [PrsId]         INT            NOT NULL,
    [Action]        NVARCHAR (30)  NOT NULL,
    [AncienStatut]  NVARCHAR (50)  NULL,
    [NouveauStatut] NVARCHAR (50)  NOT NULL,
    [UserLogin]     NVARCHAR (100) NOT NULL,
    [DateAction]    DATETIME2 (7)  NOT NULL,
    [Changements]   NVARCHAR (MAX) NOT NULL,
    CONSTRAINT [PK_HistoriqueEdit] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_HistoriqueEdit_Prs] FOREIGN KEY ([PrsId]) REFERENCES [dbo].[PRS] ([Id])
);


GO
CREATE NONCLUSTERED INDEX [IX_HistoriqueEdit_PrsId]
    ON [dbo].[HistoriqueEdit]([PrsId] ASC);

