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

        public async Task OnGetAsync()
        {
            await LoadDataAsync();
        }

        private async Task LoadDataAsync()
        {
            FamillesPRS = await _context.PrsFamilles.OrderBy(f => f.Libelle).ToListAsync();
            GroupesUtilisateurs = await _context.GroupesUtilisateurs
                .OrderBy(g => g.NomGroupe)
                .ToListAsync();
            Utilisateurs = await _context.Utilisateurs
                .Where(u => u.DateDeleted == null)
                .OrderBy(u => u.Nom)
                .ThenBy(u => u.Prenom)
                .ToListAsync();
            GroupeUtilisateurs = await _context.GroupeUtilisateurs.ToListAsync();
        }

        // Méthodes helper pour les équipes
        public int GetTeamMembersCount(int teamId)
        {
            return GroupeUtilisateurs.Count(gu => gu.GroupeId == teamId);
        }

        public IEnumerable<Utilisateur> GetTeamMembers(int teamId)
        {
            var memberIds = GroupeUtilisateurs
                .Where(gu => gu.GroupeId == teamId)
                .Select(gu => gu.UtilisateurId)
                .ToList();

            return Utilisateurs.Where(u => memberIds.Contains(u.Id));
        }

        // Gestion des familles PRS
        public async Task<IActionResult> OnPostSaveFamilyAsync(int familyId, string familyName, string familyColor)
        {
            if (string.IsNullOrWhiteSpace(familyName) || string.IsNullOrWhiteSpace(familyColor))
            {
                ViewData["Error"] = "Le nom et la couleur sont obligatoires.";
                await LoadDataAsync();
                return Page();
            }

            try
            {
                if (familyId == 0)
                {
                    // Nouvelle famille
                    var famille = new PrsFamille
                    {
                        Libelle = familyName.Trim(),
                        CouleurHex = familyColor.Trim()
                    };
                    _context.PrsFamilles.Add(famille);
                    ViewData["Message"] = "Famille ajoutée avec succès.";
                }
                else
                {
                    // Modification famille existante
                    var famille = await _context.PrsFamilles.FindAsync(familyId);
                    if (famille != null)
                    {
                        famille.Libelle = familyName.Trim();
                        famille.CouleurHex = familyColor.Trim();
                        ViewData["Message"] = "Famille modifiée avec succès.";
                    }
                    else
                    {
                        ViewData["Error"] = "Famille introuvable.";
                    }
                }

                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                ViewData["Error"] = "Erreur lors de l'enregistrement : " + ex.Message;
            }

            await LoadDataAsync();
            return Page();
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
                    ViewData["Message"] = "Famille supprimée avec succès.";
                }
                else
                {
                    ViewData["Error"] = "Famille introuvable.";
                }
            }
            catch (Exception ex)
            {
                ViewData["Error"] = "Erreur lors de la suppression : " + ex.Message;
            }

            await LoadDataAsync();
            return Page();
        }

        // Gestion des équipes
        public async Task<IActionResult> OnPostSaveTeamAsync(int teamId, string teamName, string teamDescription)
        {
            if (string.IsNullOrWhiteSpace(teamName))
            {
                ViewData["Error"] = "Le nom de l'équipe est obligatoire.";
                await LoadDataAsync();
                return Page();
            }

            try
            {
                var currentUser = User?.Identity?.Name ?? "System";

                if (teamId == 0)
                {
                    // Nouvelle équipe
                    var groupe = new GroupeUtilisateurs
                    {
                        NomGroupe = teamName.Trim(),
                        Description = teamDescription?.Trim() ?? "",
                        DateCreation = DateTime.Now,
                        CreePar = currentUser,
                        Actif = true
                    };
                    _context.GroupesUtilisateurs.Add(groupe);
                    ViewData["Message"] = "Équipe créée avec succès.";
                }
                else
                {
                    // Modification équipe existante
                    var groupe = await _context.GroupesUtilisateurs.FindAsync(teamId);
                    if (groupe != null)
                    {
                        groupe.NomGroupe = teamName.Trim();
                        groupe.Description = teamDescription?.Trim() ?? "";
                        ViewData["Message"] = "Équipe modifiée avec succès.";
                    }
                    else
                    {
                        ViewData["Error"] = "Équipe introuvable.";
                    }
                }

                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                ViewData["Error"] = "Erreur lors de l'enregistrement : " + ex.Message;
            }

            await LoadDataAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostDeleteTeamAsync(int teamId)
        {
            try
            {
                var groupe = await _context.GroupesUtilisateurs.FindAsync(teamId);
                if (groupe != null)
                {
                    // Supprimer d'abord tous les membres de l'équipe
                    var membres = _context.GroupeUtilisateurs.Where(gu => gu.GroupeId == teamId);
                    _context.GroupeUtilisateurs.RemoveRange(membres);

                    // Puis supprimer l'équipe
                    _context.GroupesUtilisateurs.Remove(groupe);
                    await _context.SaveChangesAsync();
                    ViewData["Message"] = "Équipe supprimée avec succès.";
                }
                else
                {
                    ViewData["Error"] = "Équipe introuvable.";
                }
            }
            catch (Exception ex)
            {
                ViewData["Error"] = "Erreur lors de la suppression : " + ex.Message;
            }

            await LoadDataAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostUpdateTeamMembersAsync(int teamId, List<int> selectedUsers)
        {
            try
            {
                // Supprimer tous les membres actuels de l'équipe
                var currentMembers = _context.GroupeUtilisateurs.Where(gu => gu.GroupeId == teamId);
                _context.GroupeUtilisateurs.RemoveRange(currentMembers);

                // Ajouter les nouveaux membres sélectionnés
                if (selectedUsers?.Any() == true)
                {
                    foreach (var userId in selectedUsers)
                    {
                        var groupeUtilisateur = new GroupeUtilisateur
                        {
                            GroupeId = teamId,
                            UtilisateurId = userId,
                            DateAjout = DateTime.Now
                        };
                        _context.GroupeUtilisateurs.Add(groupeUtilisateur);
                    }
                }

                await _context.SaveChangesAsync();
                ViewData["Message"] = "Membres de l'équipe mis à jour avec succès.";
            }
            catch (Exception ex)
            {
                ViewData["Error"] = "Erreur lors de la mise à jour des membres : " + ex.Message;
            }

            await LoadDataAsync();
            return Page();
        }

        // API pour récupérer les membres d'une équipe (utilisé par AJAX)
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
            catch (Exception)
            {
                return new JsonResult(new List<object>());
            }
        }
    }
}