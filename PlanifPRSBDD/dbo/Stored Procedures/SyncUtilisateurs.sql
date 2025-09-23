
CREATE PROCEDURE dbo.SyncUtilisateurs
AS
BEGIN
    SET NOCOUNT ON;

    TRUNCATE TABLE dbo.Utilisateurs;

    INSERT INTO dbo.Utilisateurs (Matricule, Nom, Prenom, LoginWindows, Mail, Service, DateDeleted)
    SELECT Matricule, Nom, Prenom, LoginWindows, Mail, Service, DateSuppression
    FROM MSLSQL10_PROD.Referentiel.dbo.Personne
    WHERE Matricule IS NOT NULL
      AND Nom IS NOT NULL
      AND Prenom IS NOT NULL
      AND LoginWindows IS NOT NULL
      AND Mail IS NOT NULL
      AND Service IS NOT NULL;
END;
