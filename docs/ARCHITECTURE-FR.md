# GhostDeck — carte du code et audit (reprise du développement)

Base lue : `upstream/main` @ `5cf633a` (2026-11-15). 60 600 lignes C#, .NET 8 WinForms, 1 dépendance NuGet
(`System.Management`). 623 commits en 5 mois par un seul développeur, ~576 co-écrits avec Claude.

## Démarrage

`Program.cs` (49 l.) → élévation admin (`Core/Elevation.cs`) → `TrayContext` (3 600 l., le cœur) :
lecture du firmware EC, recherche du modèle (`Core/Devices.cs`), chargement des réglages
(`Core/AppSettings.cs` → `settings.json`), icône de la barre des tâches, raccourcis, minuteries, OSD.

## Couches

| Couche | Fichiers | Rôle |
|---|---|---|
| **Accès matériel** | `Core/Ec.cs` (833) | **Seul point d'écriture EC** : `WriteRaw(addr, val)` → `MSI_ACPI.Set_Data`. Lecture `Get_Data`, firmware `Get_EC` |
| | `Core/EcBlocks.cs` (458) | Chemin de secours (vieux firmwares, blocs WMI) — inutile sur 1596 |
| | `Core/MsiTelemetry.cs`, `GpuTelemetry.cs`, `GpuLoad.cs`, `Perf.cs` | Températures, ventilos, charge (à vérifier : ne pas réveiller la RTX sur batterie) |
| **Modèles** | `Core/Devices.cs` (2 229) | Table des 160 modèles : adresses par famille G1/G2, recettes de profils, courbes, clavier, Fn/Win |
| | `Core/ModelDb.cs` (534) | Base de modèles signée **téléchargée** depuis GitHub, peut remplacer la table compilée |
| **Logique** | `TrayContext.cs` | `SetProfile`, `Cycle`, `ApplyAssignedCurve`, `ApplyForPower`, reprise après veille (`OnPowerModeChanged`), OSD |
| | `Core/Profiles.cs`, `Scene.cs`, `Schedule.cs`, `FanCurvePreset.cs`, `CurveModel.cs` | Profils, scènes, horaires, préréglages de courbe |
| | `Core/HotkeyManager.cs` | Raccourcis `RegisterHotKey` |
| | `Core/AppSettings.cs` (1 053) | Tous les réglages persistés |
| **Interface** | `UI/MainForm.cs` + `UI/*Page.cs` | Fenêtre à onglets : Dashboard, Scenarios, Status, Fan curve, Models, Report, Updates, Settings |
| | `Forms/OsdForm.cs` | OSD de changement de profil |
| **Gadgets** | `Crosshair*`, `FpsMonitor`, `GameSessions`, `OverlayForm`, `Stats*`, `WorldMap`, `Dash*`, `PowerTest`, `FanSweep*`, `TwoTableTest`, `BackupPathTest` | Fonctions annexes (~15 000 l.) |
| **Langues** | `Core/Lang.cs` (5 813) | 15 langues en dur |

## Audit (programme lancé en administrateur)

### Écritures EC — sain
Toutes passent par `Ec.WriteRaw` (7 appelants, tous dans `Ec.cs`) :
courbe (tables), mode ventilateur, recette de profil (+ masque Super Battery), limite de charge,
Cooler Boost (lecture-modification-écriture du bit), clavier, webcam/Fn. Plus `EcBlocks` (secours, non utilisé ici).
Garde-fou firmware : écritures automatiques bloquées si le firmware EC change.

### Réseau — à couper dans notre version
| Fichier | Appel | Effet |
|---|---|---|
| `Core/Updater.cs` | `api.github.com/repos/wygodad/ghostdeck/releases` | **Télécharge un .exe et le remplace via un script `cmd`** → écraserait notre version |
| `Core/ModelDb.cs` | `raw.githubusercontent.com/wygodad/ghostdeck/main/data/` | Base de modèles signée, peut remplacer la table compilée |
| `Core/Notices.cs` | `raw.githubusercontent.com/.../announcements.json` | Annonces affichées dans l'app |
| `Core/Stats.cs` | `stats.ghostdeck.dev/report.php` | Statistiques anonymes — **désactivées par défaut** (consentement demandé) |

### Processus lancés
- `schtasks.exe` (`Core/Autostart.cs`) : tâche de démarrage élevée — voulu.
- `cmd.exe` (`Core/Updater.cs`) : remplacement de l'exe à la mise à jour — **à supprimer**.
- Ouverture de liens/dossiers (`UseShellExecute`) : pages GitHub, explorateur — inoffensif.

## Décisions pour notre version

1. **Couper** : `Updater` (désactivé, sans téléchargement), `Notices`, `Stats`, `ModelDb` (on maintient `Devices.cs` nous-mêmes).
2. **Garder** : `Ec.cs`, `Devices.cs`, profils, courbes, Fan Boost, charge, clavier, OSD, raccourcis, scènes, reprise après veille.
3. **Ajouter** (voir `PLAN-SUMMITPRO-FR.md`) : F7 (`MSI_Event` 29), profil par programme, extinction auto du clavier, raccourci présence Windows.
4. **Plus tard, à décider** : retirer les gadgets (viseur, FPS/sessions de jeu, statistiques, carte du monde, tests matériels avancés) pour alléger.
