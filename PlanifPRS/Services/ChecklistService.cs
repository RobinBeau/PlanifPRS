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
                .Include(pc => pc.Affectations) // Inclure les affectations pour exposer côté API
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
            using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                var modele = await GetChecklistModeleByIdAsync(checklistModeleId);
                if (modele == null) return false;

                // 1) Supprimer TOUTES les affectations des éléments de checklist de cette PRS (JOIN pour éviter IN/CTE)
                var affToRemove = await (from a in _context.ChecklistAffectations
                                         join pc in _context.PrsChecklists on a.ChecklistId equals pc.Id
                                         where pc.PRSId == prsId
                                         select a).ToListAsync();

                if (affToRemove.Any())
                {
                    _logger.LogInformation("[ApplyModele] Suppression affectations: {count}", affToRemove.Count);
                    _context.ChecklistAffectations.RemoveRange(affToRemove);
                    await _context.SaveChangesAsync();
                }

                // 2) Supprimer les éléments de checklist existants
                var existingItems = await _context.PrsChecklists
                    .Where(pc => pc.PRSId == prsId)
                    .ToListAsync();

                if (existingItems.Any())
                {
                    _logger.LogInformation("[ApplyModele] Suppression tâches: {count}", existingItems.Count);
                    _context.PrsChecklists.RemoveRange(existingItems);
                    await _context.SaveChangesAsync();
                }

                // 3) Récupérer PRS pour calcul des dates
                var prs = await _context.Prs.FindAsync(prsId);
                if (prs == null) { await tx.RollbackAsync(); return false; }

                // 4) Créer les nouveaux éléments basés sur le modèle
                foreach (var element in modele.Elements.OrderBy(e => e.Priorite))
                {
                    var prsChecklistItem = new PrsChecklist
                    {
                        PRSId = prsId,
                        Categorie = element.Categorie,
                        SousCategorie = element.SousCategorie,
                        Libelle = element.Libelle,
                        Tache = element.Libelle, // Compat ancien
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
                await tx.CommitAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ChecklistService] ApplyChecklistModeleAsync failed");
                await tx.RollbackAsync();
                return false;
            }
        }

        // Copie la checklist d'une PRS source vers une PRS cible, avec les affectations (utilisateurs/groupes)
        public async Task<bool> CopyChecklistFromPrsAsync(int targetPrsId, int sourcePrsId, string userLogin)
        {
            using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                var sourceChecklist = await GetPrsChecklistAsync(sourcePrsId);
                if (!sourceChecklist.Any())
                {
                    await tx.CommitAsync();
                    return true;
                }

                // 1) Supprimer affectations et éléments existants du PRS cible (JOIN pour éviter IN/CTE)
                var targetAff = await (from a in _context.ChecklistAffectations
                                       join pc in _context.PrsChecklists on a.ChecklistId equals pc.Id
                                       where pc.PRSId == targetPrsId
                                       select a).ToListAsync();

                if (targetAff.Any())
                {
                    _logger.LogInformation("[CopyFromPrs] Suppression affectations cible: {count}", targetAff.Count);
                    _context.ChecklistAffectations.RemoveRange(targetAff);
                    await _context.SaveChangesAsync();
                }

                var existingItems = await _context.PrsChecklists
                    .Where(pc => pc.PRSId == targetPrsId)
                    .ToListAsync();

                if (existingItems.Any())
                {
                    _logger.LogInformation("[CopyFromPrs] Suppression tâches cible: {count}", existingItems.Count);
                    _context.PrsChecklists.RemoveRange(existingItems);
                    await _context.SaveChangesAsync();
                }

                // 2) Récupérer PRS source et cible
                var targetPrs = await _context.Prs.FindAsync(targetPrsId);
                var sourcePrs = await _context.Prs.FindAsync(sourcePrsId);
                if (targetPrs == null || sourcePrs == null)
                {
                    await tx.RollbackAsync();
                    return false;
                }

                // 3) Copier éléments
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

                await _context.SaveChangesAsync();

                // 4) Recréer les affectations (copie)
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
                await tx.CommitAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ChecklistService] CopyChecklistFromPrsAsync failed");
                await tx.RollbackAsync();
                return false;
            }
        }

        // Crée/Remplace la checklist depuis l'IHM (type "custom" ET "copy IHM")
        public async Task<bool> CreateCustomChecklistAsync(int prsId, List<PrsChecklist> elements, string userLogin)
        {
            using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                _logger.LogInformation("[CreateCustomChecklist] Début | PRS: {prsId} | User: {user} | Elements: {count}", prsId, userLogin, elements?.Count ?? 0);

                // 1) Supprimer toutes les affectations liées aux éléments de checklist de cette PRS (JOIN pour éviter IN/CTE)
                var affToRemove = await (from a in _context.ChecklistAffectations
                                         join pc in _context.PrsChecklists on a.ChecklistId equals pc.Id
                                         where pc.PRSId == prsId
                                         select a).ToListAsync();

                if (affToRemove.Any())
                {
                    _logger.LogInformation("[CreateCustomChecklist] Suppression affectations: {count}", affToRemove.Count);
                    _context.ChecklistAffectations.RemoveRange(affToRemove);
                    await _context.SaveChangesAsync();
                }

                // 2) Supprimer les éléments existants
                var existingItems = await _context.PrsChecklists
                    .Where(pc => pc.PRSId == prsId)
                    .ToListAsync();

                if (existingItems.Any())
                {
                    _logger.LogInformation("[CreateCustomChecklist] Suppression tâches: {count}", existingItems.Count);
                    _context.PrsChecklists.RemoveRange(existingItems);
                    await _context.SaveChangesAsync();
                }

                // 3) PRS cible
                var prs = await _context.Prs.FindAsync(prsId);
                if (prs == null)
                {
                    _logger.LogError("[CreateCustomChecklist] PRS {prsId} introuvable -> return false", prsId);
                    await tx.RollbackAsync();
                    return false;
                }

                if (elements == null || elements.Count == 0)
                {
                    _logger.LogWarning("[CreateCustomChecklist] Aucune tâche à créer -> return true");
                    await tx.CommitAsync();
                    return true;
                }

                // 4) Logs échantillon
                foreach (var (el, idx) in elements.Select((e, i) => (e, i)).Take(3))
                {
                    _logger.LogInformation("[CreateCustomChecklist] Sample[{idx}] Libelle={lib} Cat={cat} SousCat={sous} Prio={prio} Delai={delai} Obl={obl}",
                        idx, el.Libelle, el.Categorie, el.SousCategorie, el.Priorite, el.DelaiDefautJours, el.Obligatoire);
                }

                // 5) Insertion nouveaux éléments
                int added = 0;
                foreach (var element in elements)
                {
                    if (element.Priorite < 1 || element.Priorite > 5) element.Priorite = 3;
                    if (element.DelaiDefautJours <= 0) element.DelaiDefautJours = 1;

                    element.PRSId = prsId;
                    element.CreatedByLogin = userLogin;
                    element.DateCreation = DateTime.Now;

                    try
                    {
                        element.DateEcheance = CalculerDateEcheance(prs, element);
                    }
                    catch (Exception calcEx)
                    {
                        _logger.LogWarning(calcEx, "[CreateCustomChecklist] Échec CalculerDateEcheance pour Libelle={lib}, fallback null", element.Libelle);
                        element.DateEcheance = null;
                    }

                    _context.PrsChecklists.Add(element);
                    added++;
                }

                _logger.LogInformation("[CreateCustomChecklist] Insertion tâches: {added}", added);

                await _context.SaveChangesAsync();
                await tx.CommitAsync();
                _logger.LogInformation("[CreateCustomChecklist] Terminé avec succès");
                return true;
            }
            catch (DbUpdateException dbu)
            {
                _logger.LogError(dbu, "[CreateCustomChecklist] DbUpdateException");
                if (dbu.InnerException != null)
                    _logger.LogError("[CreateCustomChecklist] Inner: {inner}", dbu.InnerException.ToString());
                await tx.RollbackAsync();
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CreateCustomChecklist] Exception générale");
                await tx.RollbackAsync();
                return false;
            }
        }

        // =========================
        // UTILITAIRES DATES
        // =========================

        private DateTime? CalculerDateEcheance(Prs prs, ChecklistElementModele element)
        {
            if (element.DelaiDefautJours <= 0) return null;

            DateTime dateDebut = prs.DateDebut != default ? prs.DateDebut : prs.DateCreation;
            // Dans ce projet: échéance = X jours AVANT la PRS
            return dateDebut.AddDays(-element.DelaiDefautJours);
        }

        // Surcharge pour le cas "custom/copy IHM" avec PrsChecklist
        private DateTime? CalculerDateEcheance(Prs prs, PrsChecklist element)
        {
            if (element.DelaiDefautJours <= 0) return null;

            DateTime dateDebut = prs.DateDebut != default ? prs.DateDebut : prs.DateCreation;
            // Dans ce projet: échéance = X jours AVANT la PRS
            return dateDebut.AddDays(-element.DelaiDefautJours);
        }

        private DateTime? RecalculerDateEcheance(Prs sourcePrs, Prs targetPrs, DateTime? sourceEcheance)
        {
            if (!sourceEcheance.HasValue) return null;

            DateTime sourceDebut = sourcePrs.DateDebut != default ? sourcePrs.DateDebut : sourcePrs.DateCreation;
            DateTime targetDebut = targetPrs.DateDebut != default ? targetPrs.DateDebut : targetPrs.DateCreation;

            var ecartJours = (sourceDebut - sourceEcheance.Value).Days; // nb jours entre début et échéance source
            return targetDebut.AddDays(-ecartJours); // conserve le même écart pour la cible
        }
    }
}