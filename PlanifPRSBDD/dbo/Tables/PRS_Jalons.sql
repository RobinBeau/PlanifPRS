CREATE TABLE [dbo].[PRS_Jalons] (
    [Id]         INT            IDENTITY (1, 1) NOT NULL,
    [PRSId]      INT            NULL,
    [NomJalon]   NVARCHAR (100) NULL,
    [DatePrevue] DATETIME       NULL,
    [EstValide]  BIT            DEFAULT ((0)) NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC)
);

