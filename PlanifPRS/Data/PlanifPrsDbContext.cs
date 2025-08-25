using Microsoft.EntityFrameworkCore;
using PlanifPRS.Models;

namespace PlanifPRS.Data
{
    public class PlanifPrsDbContext : DbContext
    {
        public PlanifPrsDbContext(DbContextOptions<PlanifPrsDbContext> options) : base(options) { }

        public DbSet<Prs> Prs { get; set; }
        public DbSet<PrsChecklist> PrsChecklists { get; set; }
        public DbSet<ChecklistStandard> ChecklistsStandards { get; set; }
        public DbSet<ChecklistModele> ChecklistModeles { get; set; }
        public DbSet<ChecklistElementModele> ChecklistElementModeles { get; set; }
        public DbSet<PrsJalon> PrsJalons { get; set; }
        public DbSet<Ligne> Lignes { get; set; }
        public DbSet<PrsFamille> PrsFamilles { get; set; }
        public DbSet<Utilisateur> Utilisateurs { get; set; }
        public DbSet<JalonUtilisateur> JalonUtilisateurs { get; set; }
        public DbSet<Secteur> Secteurs { get; set; }
        public DbSet<PrsFichier> PrsFichiers { get; set; }
        public DbSet<LienDossierPrs> LiensDossierPrs { get; set; }
        public DbSet<GroupeUtilisateurs> GroupesUtilisateurs { get; set; }
        public DbSet<GroupeUtilisateur> GroupeUtilisateurs { get; set; }
        public DbSet<PrsAffectation> PrsAffectations { get; set; }
        public DbSet<ChecklistAffectation> ChecklistAffectations { get; set; }
        public DbSet<HistoriqueEdit> HistoriqueEdit { get; set; }   // <-- Ajout pour l'historique des éditions

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configuration table PRS_Famille
            modelBuilder.Entity<PrsFamille>(entity =>
            {
                entity.ToTable("PRS_Famille");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Libelle)
                      .IsRequired()
                      .HasMaxLength(100);
                entity.Property(e => e.CouleurHex)
                      .HasMaxLength(7);
            });

            // Configuration pour la table Secteur
            modelBuilder.Entity<Secteur>(entity =>
            {
                entity.ToTable("Secteur", "dbo");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.IdTypeSecteur).HasColumnName("idTypeSecteur");
                entity.Property(e => e.Nom).HasColumnName("nom").IsRequired();
                entity.Property(e => e.Description).HasColumnName("description");
                entity.Property(e => e.Activation).HasColumnName("activation");
                entity.Property(e => e.DoNotDelete).HasColumnName("doNotDelete");
                entity.Property(e => e.DateCreated).HasColumnName("dateCreated");
                entity.Property(e => e.DateModified).HasColumnName("dateModified");
                entity.Property(e => e.DateDeleted).HasColumnName("dateDeleted");
            });

            // Configuration pour la table Ligne
            modelBuilder.Entity<Ligne>(entity =>
            {
                entity.ToTable("Lignes", "dbo");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Nom).IsRequired().HasMaxLength(255);
                entity.Property(e => e.IdSecteur).HasColumnName("idSecteur");
                entity.Property(e => e.Description).HasMaxLength(100).HasColumnName("description");
                entity.Property(e => e.Activation).HasColumnName("activation");
                entity.Property(e => e.DoNotDelete).HasColumnName("doNotDelete");
                entity.Property(e => e.DateCreated).HasColumnName("dateCreated");
                entity.Property(e => e.DateModified).HasColumnName("dateModified");
                entity.Property(e => e.DateDeleted).HasColumnName("dateDeleted");

                entity.HasOne(l => l.Secteur)
                      .WithMany()
                      .HasForeignKey(l => l.IdSecteur)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            // Configuration PRS_Checklist
            modelBuilder.Entity<PrsChecklist>(entity =>
            {
                entity.ToTable("PRS_Checklist");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Tache).HasMaxLength(200);
                entity.Property(e => e.Commentaire).HasMaxLength(500);
                entity.Property(e => e.Categorie).HasMaxLength(100);
                entity.Property(e => e.SousCategorie).HasMaxLength(100);
                entity.Property(e => e.Libelle).HasMaxLength(255);
                entity.Property(e => e.ValidePar).HasMaxLength(100);
                entity.Property(e => e.CreatedByLogin).HasMaxLength(100);

                entity.HasOne(c => c.Prs)
                      .WithMany(p => p.Checklist)
                      .HasForeignKey(c => c.PRSId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(c => c.Famille)
                      .WithMany()
                      .HasForeignKey(c => c.FamilleId)
                      .OnDelete(DeleteBehavior.SetNull)
                      .IsRequired(false);

                entity.HasOne(c => c.ChecklistModeleSource)
                      .WithMany()
                      .HasForeignKey(c => c.ChecklistModeleSourceId)
                      .OnDelete(DeleteBehavior.SetNull)
                      .IsRequired(false);

                entity.HasOne(c => c.PrsSource)
                      .WithMany()
                      .HasForeignKey(c => c.PrsSourceId)
                      .OnDelete(DeleteBehavior.NoAction)
                      .IsRequired(false);
            });

            // Configuration ChecklistModele
            modelBuilder.Entity<ChecklistModele>(entity =>
            {
                entity.ToTable("ChecklistModeles");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Nom).IsRequired().HasMaxLength(100);
                entity.Property(e => e.Description).HasMaxLength(500);
                entity.Property(e => e.FamilleEquipement).HasMaxLength(100);
                entity.Property(e => e.CreatedByLogin).HasMaxLength(100);
            });

            // Configuration ChecklistElementModele
            modelBuilder.Entity<ChecklistElementModele>(entity =>
            {
                entity.ToTable("ChecklistElementModeles");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Categorie).IsRequired().HasMaxLength(100);
                entity.Property(e => e.SousCategorie).HasMaxLength(100);
                entity.Property(e => e.Libelle).IsRequired().HasMaxLength(255);

                entity.HasOne(e => e.ChecklistModele)
                      .WithMany(m => m.Elements)
                      .HasForeignKey(e => e.ChecklistModeleId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // Relation PRS → PrsFamille
            modelBuilder.Entity<Prs>()
                .HasOne(p => p.Famille)
                .WithMany()
                .HasForeignKey(p => p.FamilleId)
                .OnDelete(DeleteBehavior.Restrict);

            // Relation PRS → Ligne
            modelBuilder.Entity<Prs>()
                .HasOne(p => p.Ligne)
                .WithMany(l => l.PRSs)
                .HasForeignKey(p => p.LigneId)
                .OnDelete(DeleteBehavior.Restrict);

            // Configuration pour la table PrsFichier
            modelBuilder.Entity<PrsFichier>(entity =>
            {
                entity.ToTable("PRS_Fichiers");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.NomOriginal).IsRequired().HasMaxLength(255);
                entity.Property(e => e.CheminFichier).IsRequired().HasMaxLength(500);
                entity.Property(e => e.TypeMime).HasMaxLength(100);
                entity.Property(e => e.UploadParLogin).HasMaxLength(100);

                entity.HasOne(f => f.Prs)
                      .WithMany(p => p.Fichiers)
                      .HasForeignKey(f => f.PrsId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // Configuration pour la table LienDossierPrs
            modelBuilder.Entity<LienDossierPrs>(entity =>
            {
                entity.ToTable("PRS_LiensDossierPRS");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Chemin).IsRequired().HasMaxLength(500);
                entity.Property(e => e.Description).HasMaxLength(500);
                entity.Property(e => e.AjouteParLogin).IsRequired().HasMaxLength(100);
                entity.Property(e => e.DateAjout).IsRequired().HasDefaultValueSql("GETDATE()");

                entity.HasOne(l => l.Prs)
                     .WithMany(p => p.LiensDossier)
                     .HasForeignKey(l => l.PrsId)
                     .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(e => e.PrsId)
                     .HasDatabaseName("IX_PRS_LiensDossierPRS_PrsId");
            });

            // Clé alternative pour LoginWindows dans Utilisateur
            modelBuilder.Entity<Utilisateur>()
                .HasAlternateKey(u => u.LoginWindows);

            // ChecklistStandard → Utilisateur via LoginWindows
            modelBuilder.Entity<ChecklistStandard>()
                .HasOne(cs => cs.Utilisateur)
                .WithMany()
                .HasForeignKey(cs => cs.CreePar)
                .HasPrincipalKey(u => u.LoginWindows)
                .OnDelete(DeleteBehavior.Restrict);

            // Mapping PRS_Jalons
            modelBuilder.Entity<PrsJalon>(entity =>
            {
                entity.ToTable("PRS_Jalons");
                entity.HasKey(e => e.Id);

                entity.HasOne(j => j.Prs)
                      .WithMany(p => p.Jalons)
                      .HasForeignKey(j => j.PRSId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // Mapping JalonUtilisateurs (clé composite)
            modelBuilder.Entity<JalonUtilisateur>(entity =>
            {
                entity.ToTable("JalonUtilisateurs");

                entity.HasKey(ju => new { ju.JalonId, ju.UtilisateurId });

                entity.HasOne(ju => ju.PrsJalon)
                      .WithMany(j => j.JalonUtilisateurs)
                      .HasForeignKey(ju => ju.JalonId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(ju => ju.Utilisateur)
                      .WithMany(u => u.JalonUtilisateurs)
                      .HasForeignKey(ju => ju.UtilisateurId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // Configuration GroupeUtilisateur (clé composite)
            modelBuilder.Entity<GroupeUtilisateur>(entity =>
            {
                entity.HasKey(gu => new { gu.GroupeId, gu.UtilisateurId });

                entity.HasOne(gu => gu.Groupe)
                      .WithMany(g => g.Membres)
                      .HasForeignKey(gu => gu.GroupeId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(gu => gu.Utilisateur)
                      .WithMany()
                      .HasForeignKey(gu => gu.UtilisateurId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // Configuration PrsAffectation
            modelBuilder.Entity<PrsAffectation>(entity =>
            {
                entity.HasOne(pa => pa.Prs)
                      .WithMany(p => p.Affectations)
                      .HasForeignKey(pa => pa.PrsId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(pa => pa.Utilisateur)
                      .WithMany()
                      .HasForeignKey(pa => pa.UtilisateurId)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(pa => pa.Groupe)
                      .WithMany(g => g.PrsAffectations)
                      .HasForeignKey(pa => pa.GroupeId)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            // Configuration ChecklistAffectation
            modelBuilder.Entity<ChecklistAffectation>(entity =>
            {
                entity.HasOne(ca => ca.Checklist)
                      .WithMany(c => c.Affectations)
                      .HasForeignKey(ca => ca.ChecklistId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(ca => ca.Utilisateur)
                      .WithMany()
                      .HasForeignKey(ca => ca.UtilisateurId)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(ca => ca.Groupe)
                      .WithMany()
                      .HasForeignKey(ca => ca.GroupeId)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            // Configuration HistoriqueEdit
            modelBuilder.Entity<HistoriqueEdit>(entity =>
            {
                entity.ToTable("HistoriqueEdit");
                entity.HasKey(h => h.Id);

                entity.Property(h => h.Action).IsRequired().HasMaxLength(30);
                entity.Property(h => h.AncienStatut).HasMaxLength(50);
                entity.Property(h => h.NouveauStatut).IsRequired().HasMaxLength(50);
                entity.Property(h => h.UserLogin).IsRequired().HasMaxLength(100);
                entity.Property(h => h.DateAction).IsRequired();
                entity.Property(h => h.Changements).IsRequired();

                entity.HasOne(h => h.Prs)
                      .WithMany()                // Pas de collection inverse nécessaire
                      .HasForeignKey(h => h.PrsId)
                      .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}