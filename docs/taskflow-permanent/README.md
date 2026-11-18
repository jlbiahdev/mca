# Taskflow permanent

Taskflow tourne en **tâche planifiée dans la session du compte de service** (les tâches UFX ont besoin d'Excel dans une session interactive). Il ne tourne donc que tant que cette session est ouverte.

Fermer la fenêtre du bureau à distance ne ferme pas la session : Taskflow continue. Deux cas l'arrêtent :

| Cas | Correction |
|---|---|
| **Redémarrage de la machine** (mises à jour Windows) | Ouverture de session automatique du compte de service au démarrage (Autologon, prérequis) |
| **« Se déconnecter » / « Fermer la session »** | Réouverture automatique de la session (ForceAutoLogon, posé par le script) |
| Taskflow s'arrête ou plante | Relance par la tâche toutes les 5 minutes (posée par le script) |

`taskflow-permanent.ps1` :

- **crée la tâche** si elle n'existe pas, ou **met à jour** ses déclencheurs et réglages si elle existe (l'exécutable et le compte de la tâche existante ne sont pas modifiés) ;
- déclencheurs : **à l'ouverture de session** du compte, et **toutes les 5 minutes** ; si Taskflow tourne déjà, Windows ne lance pas de deuxième instance ;
- réglages : sans limite de durée, une seule instance ;
- active **ForceAutoLogon**.

`-ValidationOnly` fait les vérifications et affiche ce qui serait fait, sans rien modifier.

---

## 1. Prérequis : ouverture de session automatique

À faire une fois, sur la machine de Taskflow, avec l'outil **Autologon** de Microsoft Sysinternals (le mot de passe est enregistré chiffré, et non en clair dans le registre) :

1. Lancer `Autologon.exe` en administrateur.
2. Saisir le compte de service, le domaine et le mot de passe, puis **Enable**.

Résultat attendu : le message « Autologon successfully configured ».

Le script refuse de continuer si l'ouverture de session automatique n'est pas configurée, ou si elle l'est pour un autre compte.

**À faire valider par la sécurité** : le mot de passe du compte est stocké sur la machine, et la session s'ouvre sans saisie après un redémarrage ou une déconnexion.

---

## 2. Configuration

En tête du script, section `>>> DEBUT CONFIGURATION` :

| Variable | Valeur |
|---|---|
| `$ServiceAccount` | Compte de service, `DOMAINE\compte` (à renseigner) |
| `$TaskName` | Nom de la tâche Taskflow existante sur la machine (`TaskFlow.Runner` par défaut) |
| `$TaskflowExecutable` | `D:\Applications\taskflow\Socgen.TaskFlow.Runner.exe` |
| `$RestartEveryMinutes` | `5` : délai maximal de relance |

Pour retrouver le nom de la tâche existante :

```powershell
Get-ScheduledTask | Where-Object { $_.Actions.Execute -like "*TaskFlow*" } | Select-Object TaskName, State
```

---

## 3. Utilisation

Console PowerShell **en administrateur**, sur la machine de Taskflow.

**Étape 1 — Vérification, sans rien modifier :**

```powershell
.\taskflow-permanent.ps1 -ValidationOnly
```

Résultat attendu : `Ouverture de session automatique configurée pour DOMAINE\compte.`, la liste de ce qui serait fait, puis `MODE VALIDATION : rien n'a été modifié.`

**Étape 2 — Programmation :**

```powershell
.\taskflow-permanent.ps1
```

Résultat attendu : `Tâche ... mise à jour.` (ou `créée.`), `ForceAutoLogon activé.`, puis `Programmation terminée.`

---

## 4. Tests

| # | Action | Résultat attendu |
|---|---|---|
| 1 | `Stop-Process -Name Socgen.TaskFlow.Runner` | Taskflow revient dans les 5 minutes (`Get-Process Socgen.TaskFlow.Runner`) |
| 2 | Fermer la fenêtre du bureau à distance, puis se reconnecter | Taskflow tourne toujours |
| 3 | Menu Démarrer > Se déconnecter | La session se rouvre seule en quelques secondes ; Taskflow repart |
| 4 | Redémarrer la machine | La session s'ouvre seule ; Taskflow repart |
| 5 | Lancer une tâche UFX | Elle réussit |

---

## 5. À savoir

- **Toujours lancer Taskflow par la tâche** (`Start-ScheduledTask TaskFlow.Runner`). Lancé à la main, Windows ne le rattache pas à la tâche, qui peut alors démarrer une deuxième instance.
- **Avec ForceAutoLogon, la console de la machine rouvre toujours la session du compte de service.** Sans incidence ici : seul ce compte se connecte à la machine.
- **Retour en arrière** : `Set-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name ForceAutoLogon -Value "0"`, et désactiver Autologon (bouton **Disable**).
- **Script non exécuté sur Windows** lors de sa rédaction : commencer par `-ValidationOnly`.

---

## 6. Installation du script

Le script est fourni dans `taskflow-permanent.md` (ce dépôt ne contient pas de fichier `.ps1`) :

1. Créer le fichier `taskflow-permanent.ps1` sur la machine de Taskflow.
2. Y coller **tout le contenu** du bloc de code de `taskflow-permanent.md`.
3. L'enregistrer en **UTF-8 avec BOM** (Visual Studio Code : en bas à droite, cliquer sur l'encodage > Save with Encoding > UTF-8 with BOM). Sans BOM, les accents sont corrompus sous Windows PowerShell 5.1.
4. Renseigner la configuration (§ 2).
