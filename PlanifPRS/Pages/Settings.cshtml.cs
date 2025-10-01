using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PlanifPRS.Data;
using PlanifPRS.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PlanifPRS.Pages
{
    public class SettingsModel : PageModel
    {
        private readonly PlanifPrsDbContext _context;

        public SettingsModel(PlanifPrsDbContext context)
        {
            _context = context;
        }

        public List<PrsFamille> FamillesPRS { get; set; } = new List<PrsFamille>();
        public List<GroupeUtilisateurs> GroupesUtilisateurs { get; set; } = new List<GroupeUtilisateurs>();
        public List<Utilisateur> Utilisateurs { get; set; } = new List<Utilisateur>();
        public List<GroupeUtilisateur> GroupeUtilisateurs { get; set; } = new List<GroupeUtilisateur>();
        public List<ChecklistModele> ChecklistModeles { get; set; } = new List<ChecklistModele>();

        public async Task OnGetAsync()
        {
            await LoadDataAsync();
        }

        private async Task LoadDataAsync()
        {
            FamillesPRS = await _context.PrsFamilles.OrderBy(f => f.Libelle).ToListAsync();
            GroupesUtilisateurs = await _context.GroupesUtilisateurs
                .Include(g => g.Membres)
                .OrderBy(g => g.NomGroupe)
                .ToListAsync();

            Utilisateurs = await _context.Utilisateurs
                .Where(u => u.DateDeleted == null)
                .OrderBy(u => u.Nom)
                .ThenBy(u => u.Prenom)
                .ToListAsync();

            GroupeUtilisateurs = await _context.GroupeUtilisateurs.ToListAsync();

            // Charger modèles + éléments (inclure GroupeId si déjà mappé dans l'entité)
            ChecklistModeles = await _context.ChecklistModeles
                .Include(cm => cm.Elements)
                .Where(cm => cm.Actif)
                .OrderBy(cm => cm.Nom)
                .ToListAsync();
        }

        // Helper équipes
        public int GetTeamMembersCount(int teamId) =>
            GroupeUtilisateurs.Count(gu => gu.GroupeId == teamId);

        public IEnumerable<Utilisateur> GetTeamMembers(int teamId)
        {
            var memberIds = GroupeUtilisateurs
                .Where(gu => gu.GroupeId == teamId)
                .Select(gu => gu.UtilisateurId)
                .ToList();

            return Utilisateurs.Where(u => memberIds.Contains(u.Id));
        }

        // Familles PRS
        public async Task<IActionResult> OnPostSaveFamilyAsync(int familyId, string familyName, string familyColor)
        {
            if (string.IsNullOrWhiteSpace(familyName) || string.IsNullOrWhiteSpace(familyColor))
            {
                TempData["Error"] = "Le nom et la couleur sont obligatoires.";
                return RedirectToPage(new { tab = "families" });
            }

            try
            {
                if (familyId == 0)
                {
                    _context.PrsFamilles.Add(new PrsFamille
                    {
                        Libelle = familyName.Trim(),
                        CouleurHex = familyColor.Trim()
                    });
                    TempData["Message"] = "Famille ajoutée avec succès.";
                }
                else
                {
                    var famille = await _context.PrsFamilles.FindAsync(familyId);
                    if (famille != null)
                    {
                        famille.Libelle = familyName.Trim();
                        famille.CouleurHex = familyColor.Trim();
                        TempData["Message"] = "Famille modifiée avec succès.";
                    }
                    else TempData["Error"] = "Famille introuvable.";
                }
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                TempData["Error"] = "Erreur lors de l'enregistrement : " + ex.Message;
            }
            return RedirectToPage(new { tab = "families" });
        }

        public async Task<IActionResult> OnPostDeleteFamilyAsync(int familyId)
        {
            try
            {
                var famille = await _context.PrsFamilles.FindAsync(familyId);
                if (famille != null)
                {
                    _context.PrsFamilles.Remove(famille);
                    await _context.SaveChangesAsync();
                    TempData["Message"] = "Famille supprimée avec succès.";
                }
                else TempData["Error"] = "Famille introuvable.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = "Erreur lors de la suppression : " + ex.Message;
            }
            return RedirectToPage(new { tab = "families" });
        }

        // Équipes
        public async Task<IActionResult> OnPostSaveTeamAsync(int teamId, string teamName, string teamDescription)
        {
            if (string.IsNullOrWhiteSpace(teamName))
            {
                TempData["Error"] = "Le nom de l'équipe est obligatoire.";
                return RedirectToPage(new { tab = "teams" });
            }

            try
            {
                var currentUser = User?.Identity?.Name ?? "System";
                if (teamId == 0)
                {
                    _context.GroupesUtilisateurs.Add(new GroupeUtilisateurs
                    {
                        NomGroupe = teamName.Trim(),
                        Description = teamDescription?.Trim() ?? "",
                        DateCreation = DateTime.Now,
                        CreePar = currentUser,
                        Actif = true
                    });
                    TempData["Message"] = "Équipe créée avec succès.";
                }
                else
                {
                    var groupe = await _context.GroupesUtilisateurs.FindAsync(teamId);
                    if (groupe != null)
                    {
                        groupe.NomGroupe = teamName.Trim();
                        groupe.Description = teamDescription?.Trim() ?? "";
                        TempData["Message"] = "Équipe modifiée avec succès.";
                    }
                    else TempData["Error"] = "Équipe introuvable.";
                }
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                TempData["Error"] = "Erreur lors de l'enregistrement : " + ex.Message;
            }
            return RedirectToPage(new { tab = "teams" });
        }

        public async Task<IActionResult> OnPostDeleteTeamAsync(int teamId)
        {
            try
            {
                var groupe = await _context.GroupesUtilisateurs.FindAsync(teamId);
                if (groupe != null)
                {
                    var membres = _context.GroupeUtilisateurs.Where(gu => gu.GroupeId == teamId);
                    _context.GroupeUtilisateurs.RemoveRange(membres);
                    _context.GroupesUtilisateurs.Remove(groupe);
                    await _context.SaveChangesAsync();
                    TempData["Message"] = "Équipe supprimée avec succès.";
                }
                else TempData["Error"] = "Équipe introuvable.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = "Erreur lors de la suppression : " + ex.Message;
            }
            return RedirectToPage(new { tab = "teams" });
        }

        public async Task<IActionResult> OnPostUpdateTeamMembersAsync(int teamId, List<int> selectedUsers)
        {
            try
            {
                var current = _context.GroupeUtilisateurs.Where(gu => gu.GroupeId == teamId);
                _context.GroupeUtilisateurs.RemoveRange(current);

                if (selectedUsers?.Any() == true)
                {
                    foreach (var userId in selectedUsers)
                    {
                        _context.GroupeUtilisateurs.Add(new GroupeUtilisateur
                        {
                            GroupeId = teamId,
                            UtilisateurId = userId,
                            DateAjout = DateTime.Now
                        });
                    }
                }

                await _context.SaveChangesAsync();
                TempData["Message"] = "Membres de l'équipe mis à jour avec succès.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = "Erreur lors de la mise à jour des membres : " + ex.Message;
            }
            return RedirectToPage(new { tab = "teams" });
        }

        public async Task<IActionResult> OnGetGetTeamMembersAsync(int teamId)
        {
            try
            {
                var members = await (from gu in _context.GroupeUtilisateurs
                                     join u in _context.Utilisateurs on gu.UtilisateurId equals u.Id
                                     where gu.GroupeId == teamId && u.DateDeleted == null
                                     select new
                                     {
                                         id = u.Id,
                                         nom = u.Nom,
                                         prenom = u.Prenom,
                                         mail = u.Mail,
                                         service = u.Service
                                     }).ToListAsync();

                return new JsonResult(members);
            }
            catch
            {
                return new JsonResult(new List<object>());
            }
        }

        // Checklists
        public async Task<IActionResult> OnPostSaveChecklistModeleAsync(
            int checklistModeleId,
            string checklistNom,
            string checklistFamille,
            string checklistDescription,
            List<ChecklistElementDto> elements)
        {
            if (string.IsNullOrWhiteSpace(checklistNom) || string.IsNullOrWhiteSpace(checklistFamille))
            {
                TempData["Error"] = "Le nom et la famille d'équipement sont obligatoires.";
                return RedirectToPage(new { tab = "checklists" });
            }
            if (elements == null || !elements.Any())
            {
                TempData["Error"] = "Au moins un élément de checklist est requis.";
                return RedirectToPage(new { tab = "checklists" });
            }

            try
            {
                var currentUser = User.Identity?.Name ?? "System";

                if (checklistModeleId == 0)
                {
                    var modele = new ChecklistModele
                    {
                        Nom = checklistNom.Trim(),
                        Description = checklistDescription?.Trim(),
                        FamilleEquipement = checklistFamille,
                        DateCreation = DateTime.Now,
                        CreatedByLogin = currentUser,
                        Actif = true
                    };
                    _context.ChecklistModeles.Add(modele);
                    await _context.SaveChangesAsync();

                    foreach (var elementDto in elements)
                    {
                        if (!string.IsNullOrWhiteSpace(elementDto.Libelle) &&
                            !string.IsNullOrWhiteSpace(elementDto.Categorie))
                        {
                            _context.ChecklistElementModeles.Add(new ChecklistElementModele
                            {
                                ChecklistModeleId = modele.Id,
                                Categorie = elementDto.Categorie,
                                SousCategorie = elementDto.SousCategorie,
                                Libelle = elementDto.Libelle,
                                Obligatoire = elementDto.Obligatoire,
                                Priorite = elementDto.Priorite,
                                DelaiDefautJours = elementDto.DelaiDefautJours,
                                GroupeId = elementDto.GroupeId  // NOUVEAU
                            });
                        }
                    }
                    TempData["Message"] = "Modèle de checklist créé avec succès.";
                }
                else
                {
                    var modele = await _context.ChecklistModeles
                        .Include(cm => cm.Elements)
                        .FirstOrDefaultAsync(cm => cm.Id == checklistModeleId);

                    if (modele == null)
                    {
                        TempData["Error"] = "Modèle de checklist introuvable.";
                        return RedirectToPage(new { tab = "checklists" });
                    }

                    modele.Nom = checklistNom.Trim();
                    modele.Description = checklistDescription?.Trim();
                    modele.FamilleEquipement = checklistFamille;

                    // On remplace les éléments (simple)
                    _context.ChecklistElementModeles.RemoveRange(modele.Elements);

                    foreach (var elementDto in elements)
                    {
                        if (!string.IsNullOrWhiteSpace(elementDto.Libelle) &&
                            !string.IsNullOrWhiteSpace(elementDto.Categorie))
                        {
                            _context.ChecklistElementModeles.Add(new ChecklistElementModele
                            {
                                ChecklistModeleId = modele.Id,
                                Categorie = elementDto.Categorie,
                                SousCategorie = elementDto.SousCategorie,
                                Libelle = elementDto.Libelle,
                                Obligatoire = elementDto.Obligatoire,
                                Priorite = elementDto.Priorite,
                                DelaiDefautJours = elementDto.DelaiDefautJours,
                                GroupeId = elementDto.GroupeId  // NOUVEAU
                            });
                        }
                    }

                    TempData["Message"] = "Modèle de checklist modifié avec succès.";
                }

                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Erreur lors de l'enregistrement : {ex.Message}";
            }

            return RedirectToPage(new { tab = "checklists" });
        }

        public async Task<IActionResult> OnPostDeleteChecklistModeleAsync(int checklistModeleId)
        {
            try
            {
                var modele = await _context.ChecklistModeles
                    .Include(cm => cm.Elements)
                    .FirstOrDefaultAsync(cm => cm.Id == checklistModeleId);

                if (modele != null)
                {
                    modele.Actif = false;
                    await _context.SaveChangesAsync();
                    TempData["Message"] = "Modèle de checklist désactivé avec succès.";
                }
                else TempData["Error"] = "Modèle de checklist introuvable.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Erreur lors de la suppression : {ex.Message}";
            }
            return RedirectToPage(new { tab = "checklists" });
        }
    }

    // DTO enrichi
    public class ChecklistElementDto
    {
        public int Id { get; set; }
        public string Categorie { get; set; }
        public string SousCategorie { get; set; }
        public string Libelle { get; set; }
        public bool Obligatoire { get; set; }
        public int Priorite { get; set; } = 3;
        public int DelaiDefautJours { get; set; } = 1;
        public int? GroupeId { get; set; }   // NOUVEAU
    }
}