# Plan — GhostDeck pour le MSI SummitPro 16 AI A2HVETG (MS-1596)

Branche : `summitpro-1596` · base : `upstream/main` @ `5cf633a` (2026-11-15) · licence GPL-3.0 conservée.

## Objectif

Remplacer la partie « modes / ventilateurs / batterie » de MSI Center S par GhostDeck, en gardant
ce que MSI fait bien (touche F7 + OSD, touches Fn) et en ajoutant ce que MSI ne fait pas :
courbe de ventilateur, Cooler Boost, courbe réappliquée au réveil, profil par programme qui marche.

## Faits mesurés sur ce PC (19/11/2026)

| Élément | Valeur | Source |
|---|---|---|
| Carte / BIOS / EC | MS-1596 · `E1596IMS.30F` · `1596EMS1.306 02242025` | registre BIOS, EC `0xA0` |
| Profil de puissance | `0xD2` : `C1` Balanced/Silent, `C4` Extreme, `C2` Super Battery | dump EC + table GhostDeck |
| Mode ventilateur | `0xD4` : `0x0D` auto, `0x8D` courbe perso (bit 7), `0x1D` Silent (bit 4) | écrit par MSI Center sous nos yeux |
| Courbe CPU | temp `0x69..0x6F` = 0/55/68/78/88/93/100 · vitesses `0x72..0x78` = 0/33/50/60/70/85/105 | dump EC = `Get_Fan(1)` |
| Courbe GPU | temp `0x81..0x87` = 0/50/59/67/75/80/85 · vitesses `0x8A..0x90` = 0/33/60/75/100/115/120 | dump EC = `Get_Fan(2)` |
| Cooler Boost | `0x98` bit 7 (`0x06` → `0x86`) | testé (2 800 → ~8 500 tr/min, −8 °C en 30 s) |
| Limite de charge | `0xD7` = `0x80 | %` (actuel `0xD0` = 80 %) | dump EC |
| Super Battery | `0xEB` (`0x0F` actif) | table GhostDeck |
| Clavier | `0xD3` = `0x80 | niveau` · extinction auto : `0x2C` bit 3 | dump EC + code MSI Center S |
| Fn/Win swap | `0xE8` bit 4 (inversé) | table GhostDeck |
| Tachymètres | `0xC8:C9` CPU, `0xCA:CB` GPU (tr/min = 480000 / brut) | dump EC |
| Touche F7 | WMI `root\WMI` → `MSI_Event`, propriété `MSIEvt`, `MSIEvt & 0xFF == 29` | code MSI Center S (`ComponentLoader.WMIEventMath`) |
| Touche éco (Fn) | même événement, code `121` | idem |
| Cycle F7 MSI | 1 High Perf → 2 Balanced → 3 Super Battery → 5 AI → 1 | idem |

Conséquence : l'entrée GhostDeck `1596EMS1` (« Summit E16 AI Studio A1VETG », `Experimental`,
`ModernCurve`, `StdRecipes(0xD2, 0xD4, 0xEB)`) correspond à 100 % à cette machine.

## Architecture cible

- **GhostDeck = seul chef** des profils, de la courbe, de Cooler Boost, de la limite de charge et du clavier.
- **MSI gardé** : `MSI Foundation Service` + `OmApSvcBroker` (OSD des touches Fn : luminosité, son, micro, webcam)
  et l'interface WMI `MSI_ACPI` (pas de pilote noyau).
- **MSI coupé** : `MSI_Center_S_Service` passé en démarrage manuel + ses 3 tâches planifiées désactivées
  (sinon F7 changerait le mode deux fois). MSI Center (gaming) désinstallé, clé `ForcePassMsiCenterCheck` retirée.
- Retour arrière : relancer `MSI_Center_S_Service` et réactiver ses tâches → MSI Center S reprend la main.

## Phases

### P0 — Préparation ✅ / à finir
- [x] Clone complet, `upstream` = wygodad/ghostdeck, branche `summitpro-1596`.
- [x] Build Release OK (.NET 8, 0 erreur, 0 avertissement).
- [ ] **Mises à jour** : dans notre build, `UpdateCheckEnabled = false` par défaut et le bouton « Installer »
      désactivé (sinon l'app téléchargerait la version officielle et écraserait la nôtre).
      La base de modèles signée téléchargée reste autorisée (elle garde les mêmes adresses pour 1596).

- [x] **Mode hors ligne** (réglage `OfflineMode`, activé par défaut, `Core/NetPolicy.cs`) : coupe la vérification
      des mises à jour, la base de modèles téléchargée (y compris celle déjà en cache), les annonces et les statistiques.
      L'installation d'une version téléchargée est bloquée en permanence (`NetPolicy.InstallDownloadedUpdates = false`).
- [x] **Mode léger** (réglage `LiteMode`, activé par défaut) : overlay de jeu, viseur, moniteur FPS, rapport de
      session de jeu, statistiques (envoi + demande de consentement) désactivés ; onglet Tableau de bord et groupe
      Gaming des réglages masqués. Restent visibles (inertes) : entrées du menu de la barre des tâches, sous-onglet
      Status → Gaming — à nettoyer si besoin.

### P1 — Modèle 1596 (lecture seule d'abord)
- [ ] Ajouter « MSI SummitPro 16 AI A2HVETG » au nom de l'entrée `1596EMS1`, avec les tachymètres
      16 bits `0xC8:C9` / `0xCA:CB`.
- [ ] Lancer GhostDeck **en lecture seule** (aucun clic sur un profil) : vérifier que Status affiche
      les mêmes températures, vitesses et la même courbe que notre dump.
- [ ] Puis tests d'écriture un par un, en comparant le dump EC avant/après :
      Balanced → Extreme → Silent → Super Battery, courbe perso, Fan Boost, limite de charge.

### P2 — Touche F7 + OSD
- [ ] Nouveau `Core/MsiKeyEvents.cs` : `ManagementEventWatcher` sur `SELECT * FROM MSI_Event` (`root\WMI`),
      décodage `MSIEvt & 0xFF`, renvoi sur le thread UI.
- [ ] Code 29 (F7) → `Cycle(ChangeSource.Hotkey)` (cycle déjà existant, suit l'ordre de profils choisi, OSD inclus).
- [ ] Code 121 (touche éco) → Super Battery.
- [ ] Réglage « Touche MSI F7 » (activé par défaut) + choix des profils inclus dans le cycle.
- [ ] Avertissement si `MSI_Center_S_Service` tourne (double changement de mode).
- [ ] Test : F7 x 8, vérifier `0xD2`/`0xD4` et l'OSD à chaque appui.

### P3 — Profil par programme (remplace AI Engine)
- [ ] `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` → nom du processus (et du paquet pour les applis Store).
- [ ] Règles : programme → profil (+ préréglage de courbe optionnel), stockées dans `AppSettings`.
- [ ] Retour au profil de base quand on quitte le programme, délai anti-rebond ~2 s, ignorer les fenêtres GhostDeck.
- [ ] Priorités : choix manuel (F7, tuile) > règle programme > règle batterie ; un choix manuel suspend les
      règles jusqu'au prochain changement de programme.
- [ ] UI : Settings → Automation, liste éditable + bouton « ajouter le programme actif ».
- [ ] Ne jamais interroger la carte NVIDIA (pas de saccades sur batterie).

### P4 — Réveil et démarrage (contre les coupures)
- [ ] Activer par défaut `RestoreCurveOnResume` et `RestoreProfileOnResume` dans notre build.
- [ ] Réécrire la courbe vite après la reprise (≈1 s puis 5 s), avant la remontée en charge.
- [ ] Option : Fan Boost 30 s au réveil.
- [ ] Test : veille 1 min / 10 min, redémarrage, arrêt complet ; dump EC après chaque reprise.

### P5 — Clavier
- [ ] Option « extinction auto du rétroéclairage » (`0x2C` bit 3), comme MSI Center S.

### P6 — Bascule finale
- [ ] Tâche de démarrage GhostDeck (option intégrée « Start with Windows »).
- [ ] `MSI_Center_S_Service` → manuel, tâches `MSI_Center_S_*` désactivées ; Foundation Service conservé.
- [ ] Désinstaller MSI Center (gaming), supprimer `HKLM\SOFTWARE\WOW6432Node\MSI\NB\ForcePassMsiCenterCheck`.
- [ ] Une semaine d'utilisation ; surveiller les arrêts (événements 6008/86) et les températures.

### P6b — Fonctions « AI » de MSI Center S (analysées dans le code décompilé)
- [ ] **Smart Guard** : carte « Présence » dans Settings → System, basée sur l'API Windows
      `Windows.Devices.Sensors.HumanPresenceSettings` (`IsLockOnLeaveEnabled`, `IsWakeOnApproachEnabled`,
      `UpdateSettings`) — exactement ce que fait MSI. Le verrouillage maison de MSI (`HumanPresenceSensor`
      + minuteur 30 s + `LockWorkStation`) n'est refait que si l'option Windows ne suffit pas.
      Nécessite de passer le projet en `net8.0-windows10.0.22621.0` (API WinRT).
- [ ] **Noise Cancellation** (iGo, propriétaire, non intégrable) : pas de réimplémentation.
      Recommandation : NVIDIA Broadcast (RTX 4050, micro + haut-parleurs) ou Windows Voice Clarity (micro).
      Note : le module MSI dépend de `MSI_Center_S_Service` → s'arrête avec lui (P6).
- [ ] **AI LAN Manager** (`New-NetQosPolicy` par appli + compteurs) : non repris (marquage DSCP rarement
      respecté par les box). Éventuellement plus tard : champ « priorité réseau » dans les scènes.
- [x] Smart Brightness, Studio Effects : simples liens vers les réglages Windows → rien à faire.
- [x] AI Adaptive Charging : fonctions Intel IPF (CBC/RTE) → laissées au pilote Intel.
- [x] « MSI AI » (commandes) / « free up memory » (`EmptyWorkingSet` sur tous les processus) : non repris, contre-productif.

### P7 — Contribution (optionnel)
- [ ] Proposer en amont : passage de `1596EMS1` en vérifié + nom SummitPro, support F7 (`MSI_Event` 29),
      profil par programme. Nécessite un compte GitHub (fork + pull request).

## Risques et garde-fous

- Écritures EC : uniquement les adresses mesurées ci-dessus ; dump avant/après chaque test ;
  courbe d'usine sauvegardée (`C:\Users\overl\crashreports\courbe_usine_backup.txt`).
- « Panic reset » GhostDeck (`Ctrl+Alt+F10`) : retour Balanced + courbe auto + Fan Boost off.
- Garde firmware de GhostDeck : bloque les écritures automatiques après une mise à jour BIOS/EC.
- Pas de flash firmware, pas de pilote noyau.
