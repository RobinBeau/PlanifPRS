using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PlanifPRS.Data;
using PlanifPRS.Models;

namespace PlanifPRS.Services
{
    public class ChecklistService
    {
        private readonly PlanifPrsDbContext _context;
        private readonly ILogger<ChecklistService> _logger;

        public ChecklistService(PlanifPrsDbContext context, ILogger<ChecklistService> logger)
        {
            _context = context;
            _logger = logger;
        }

        // =========================
        // MODELES DE CHECKLIST
        // =========================

        // Utilisé par Controllers/ChecklistsController.cs + Pages/Create.cshtml.cs + Pages/Edit.cshtml.cs
        public async Task<List<ChecklistModele>> GetChecklistModelesAsync()
        {
            return await _context.ChecklistModeles
                .Include(cm => cm.Elements)
                .Where(cm => cm.Actif)
                .OrderBy(cm => cm.Nom)
                .ToListAsync();
        }

        // Utilisé par Controllers/ChecklistsController.cs
        public async Task<ChecklistModele?> GetChecklistModeleByIdAsync(int id)
        {
            return await _context.ChecklistModeles
                .Include(cm => cm.Elements)
                .FirstOrDefaultAsync(cm => cm.Id == id && cm.Actif);
        }

        public async Task<bool> CreateChecklistModeleAsync(ChecklistModele modele)
        {
            try
            {
                _context.ChecklistModeles.Add(modele);
                await _context.SaveChangesAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ChecklistService] CreateChecklistModeleAsync failed");
                return false;
            }
        }

        public async Task<bool> UpdateChecklistModeleAsync(ChecklistModele modele)
        {
            try
            {
                _context.ChecklistModeles.Update(modele);
                await _context.SaveChangesAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ChecklistService] UpdateChecklistModeleAsync failed");
                return false;
            }
        }

        public async Task<bool> DeleteChecklistModeleAsync(int id)
        {
            var modele = await _context.ChecklistModeles.FindAsync(id);
            if (modele == null) return false;

            modele.Actif = false;
            await _context.SaveChangesAsync();
            return true;
        }

        // =========================
        // CHECKLISTS PRS
        // =========================

        public async Task<List<PrsChecklist>> GetPrsChecklistAsync(int prsId)
        {
            return await _context.PrsChecklists
                .Include(pc => pc.Famille)
                .Include(pc => pc.ChecklistModeleSource)
                .Include(pc => pc.Affectations) // Inclure les affectations pour pouvoir les exposer côté API
                .Where(pc => pc.PRSId == prsId)
                .OrderBy(pc => pc.Priorite)
                .ThenBy(pc => pc.DelaiDefautJours)
                .ThenBy(pc => pc.DateEcheance)
                .ThenBy(pc => pc.Categorie)
                .ThenBy(pc => pc.SousCategorie)
                .ToListAsync();
        }

        // Appliquer un modèle à une PRS (remplace l'existant + supprime affectations)
        public async Task<bool> ApplyChecklistModeleAsync(int prsId, int checklistModeleId, string userLogin)
        {
            try
            {
                var modele = await GetChecklistModeleByIdAsync(checklistModeleId);
                if (modele == null) return false;

                // Supprimer les éléments existants de la checklist PRS + leurs affectations
                var existingItems = await _context.PrsChecklists
                    .Where(pc => pc.PRSId == prsId)
                    .ToListAsync();

                if (existingItems.Any())
                {
                    var existingIds = existingItems.Select(e => e.Id).ToList();
                    var affToRemove = await _context.ChecklistAffectations
                        .Where(a => existingIds.Contains(a.ChecklistId))
                        .ToListAsync();
                    if (affToRemove.Any())
                        _context.ChecklistAffectations.RemoveRange(affToRemove);

                    _context.PrsChecklists.RemoveRange(existingItems);
                    await _context.SaveChangesAsync();
                }

                // Récupérer les infos du PRS pour calculer les échéances
                var prs = await _context.Prs.FindAsync(prsId);
                if (prs == null) return false;

                // Créer les nouveaux éléments basés sur le modèle
                foreach (var element in modele.Elements.OrderBy(e => e.Priorite))
                {
                    var prsChecklistItem = new PrsChecklist
                    {
                        PRSId = prsId,
                        Categorie = element.Categorie,
                        SousCategorie = element.SousCategorie,
                        Libelle = element.Libelle,
                        Tache = element.Libelle, // Compatibilité avec l'ancien système
                        Priorite = element.Priorite,
                        DelaiDefautJours = element.DelaiDefautJours,
                        Obligatoire = element.Obligatoire,
                        EstCoche = false,
                        Statut = null,
                        DateEcheance = CalculerDateEcheance(prs, element),
                        ChecklistModeleSourceId = checklistModeleId,
                        CreatedByLogin = userLogin,
                        DateCreation = DateTime.Now
                    };

                    _context.PrsChecklists.Add(prsChecklistItem);
                }

                await _context.SaveChangesAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ChecklistService] ApplyChecklistModeleAsync failed");
                return false;
            }
        }

        // Copie la checklist d'une PRS source vers une PRS cible, avec les affectations (utilisateurs/groupes)
        public async Task<bool> CopyChecklistFromPrsAsync(int targetPrsId, int sourcePrsId, string userLogin)
        {
            try
            {
                var sourceChecklist = await GetPrsChecklistAsync(sourcePrsId);
                // Si pas d'éléments à copier, on considère que c'est "ok" (rien à faire)
                if (!sourceChecklist.Any()) return true;

                // Supprimer les éléments existants du PRS cible + leurs affectations
                var existingItems = await _context.PrsChecklists
                    .Where(pc => pc.PRSId == targetPrsId)
                    .ToListAsync();

                if (existingItems.Any())
                {
                    var targetIds = existingItems.Select(i => i.Id).ToList();
                    var targetAff = await _context.ChecklistAffectations
                        .Where(a => targetIds.Contains(a.ChecklistId))
                        .ToListAsync();
                    if (targetAff.Any())
                        _context.ChecklistAffectations.RemoveRange(targetAff);

                    _context.PrsChecklists.RemoveRange(existingItems);
                    await _context.SaveChangesAsync();
                }

                // Récupérer les infos du PRS cible et source
                var targetPrs = await _context.Prs.FindAsync(targetPrsId);
                var sourcePrs = await _context.Prs.FindAsync(sourcePrsId);
                if (targetPrs == null || sourcePrs == null) return false;

                // Copier les éléments et constituer un mapping oldId -> newEntity
                var mapOldIdToNew = new Dictionary<int, PrsChecklist>();
                foreach (var sourceItem in sourceChecklist)
                {
                    var newItem = new PrsChecklist
                    {
                        PRSId = targetPrsId,
                        Categorie = sourceItem.Categorie,
                        SousCategorie = sourceItem.SousCategorie,
                        Libelle = sourceItem.Libelle,
                        Tache = sourceItem.Tache,
                        Priorite = sourceItem.Priorite,
                        DelaiDefautJours = sourceItem.DelaiDefautJours,
                        Obligatoire = sourceItem.Obligatoire,
                        EstCoche = false,
                        Statut = null,
                        DateEcheance = RecalculerDateEcheance(sourcePrs, targetPrs, sourceItem.DateEcheance),
                        PrsSourceId = sourcePrsId,
                        CreatedByLogin = userLogin,
                        DateCreation = DateTime.Now
                    };

                    _context.PrsChecklists.Add(newItem);
                    mapOldIdToNew[sourceItem.Id] = newItem;
                }

                // Sauvegarder pour obtenir les nouveaux Id
                await _context.SaveChangesAsync();

                // Recréer les affectations à partir de la PRS source
                var sourceIds = sourceChecklist.Select(i => i.Id).ToList();
                var sourceAff = await _context.ChecklistAffectations
                    .Where(a => sourceIds.Contains(a.ChecklistId))
                    .ToListAsync();

                foreach (var a in sourceAff)
                {
                    if (!mapOldIdToNew.TryGetValue(a.ChecklistId, out var newItem)) continue;

                    var newAff = new ChecklistAffectation
                    {
                        ChecklistId = newItem.Id,
                        UtilisateurId = a.UtilisateurId,
                        GroupeId = a.GroupeId,
                        TypeAffectation = a.TypeAffectation,
                        DateAffectation = DateTime.Now,
                        AffectePar = userLogin
                    };
                    _context.ChecklistAffectations.Add(newAff);
                }

                await _context.SaveChangesAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ChecklistService] CopyChecklistFromPrsAsync failed");
                return false;
            }
        }

        // Crée une checklist personnalisée (remplace l'existant) — les affectations peuvent être ajoutées ensuite
        public async Task<bool> CreateCustomChecklistAsync(int prsId, List<PrsChecklist> elements, string userLogin)
        {
            try
            {
                _logger.LogInformation($"[CreateCustomChecklist] Début - PRS ID: {prsId}, Éléments: {elements.Count}, User: {userLogin}");

                // Supprimer les éléments existants de la checklist PRS + leurs affectations
                var existingItems = await _context.PrsChecklists
                    .Where(pc => pc.PRSId == prsId)
                    .ToListAsync();

                _logger.LogInformation($"[CreateCustomChecklist] Éléments existants trouvés: {existingItems.Count}");

                if (existingItems.Any())
                {
                    var existingIds = existingItems.Select(i => i.Id).ToList();
                    var affToRemove = await _context.ChecklistAffectations
                        .Where(a => existingIds.Contains(a.ChecklistId))
                        .ToListAsync();
                    if (affToRemove.Any())
                        _context.ChecklistAffectations.RemoveRange(affToRemove);

                    _context.PrsChecklists.RemoveRange(existingItems);
                    await _context.SaveChangesAsync();
                }

                // Récupérer la PRS pour le calcul des dates
                var prs = await _context.Prs.FindAsync(prsId);
                if (prs == null)
                {
                    _logger.LogError($"[CreateCustomChecklist] PRS {prsId} non trouvée");
                    return false;
                }

                // Ajouter les nouveaux éléments
                foreach (var element in elements)
                {
                    element.PRSId = prsId;
                    element.CreatedByLogin = userLogin;
                    element.DateCreation = DateTime.Now;

                    if (element.DelaiDefautJours > 0)
                    {
                        DateTime dateDebut = prs.DateDebut != default(DateTime) ? prs.DateDebut : prs.DateCreation;
                        element.DateEcheance = dateDebut.AddDays(-element.DelaiDefautJours); // X jours AVANT la PRS
                        _logger.LogInformation($"[CreateCustomChecklist] DateEcheance calculée pour {element.Libelle}: {element.DateEcheance} ({element.DelaiDefautJours} jours avant {dateDebut})");
                    }
                    else
                    {
                        element.DateEcheance = null;
                        _logger.LogInformation($"[CreateCustomChecklist] Pas de délai défini pour {element.Libelle}");
                    }

                    element.EstCoche = false;
                    element.Statut = null;
                    _context.PrsChecklists.Add(element);
                }

                var changes = await _context.SaveChangesAsync();
                _logger.LogInformation($"[CreateCustomChecklist] Sauvegarde réussie - {changes} modifications");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CreateCustomChecklist] Erreur");
                return false;
            }
        }

        // =========================
        // UTILITAIRES DATES
        // =========================

        private DateTime? CalculerDateEcheance(Prs prs, ChecklistElementModele element)
        {
            if (element.DelaiDefautJours <= 0) return null;

            DateTime dateDebut = prs.DateDebut != default(DateTime) ? prs.DateDebut : prs.DateCreation;
            return dateDebut.AddDays(-element.DelaiDefautJours); // X jours AVANT la PRS
        }

        private DateTime? RecalculerDateEcheance(Prs sourcePrs, Prs targetPrs, DateTime? sourceEcheance)
        {
            if (!sourceEcheance.HasValue) return null;

            DateTime sourceDebut = sourcePrs.DateDebut != default(DateTime) ? sourcePrs.DateDebut : sourcePrs.DateCreation;
            DateTime targetDebut = targetPrs.DateDebut != default(DateTime) ? targetPrs.DateDebut : targetPrs.DateCreation;

            var ecartJours = (sourceDebut - sourceEcheance.Value).Days; // nb jours entre début et échéance source
            return targetDebut.AddDays(-ecartJours); // conserve le même écart pour la cible
        }
    }
}