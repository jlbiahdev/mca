# Runner CI de dev — v0

La création de runners GitLab est bloquée sur l'instance de l'entreprise. Le runner CI est donc un script, `ci-dev.ps1`, lancé **toutes les minutes** par une tâche planifiée sur la machine de POC.

**v0 : récupération des sources uniquement.** À chaque lancement, le script :

1. demande à GitLab la liste des **merge requests ouvertes** ;
2. ignore celles dont le dernier commit a déjà été traité (fichier « j'ai travaillé » `D:\ci\runner\processed.txt`) ;
3. pour chaque autre MR : télécharge les sources de son dernier commit (zip généré par GitLab), vide **`D:\ci\build`** et y dépose toute la solution telle qu'elle est à ce commit ;
4. note le commit dans `processed.txt`.

Le build et la mise à jour du statut dans GitLab viendront ensuite (tickets 3 et 4).

Le script n'utilise que le sens sortant (machine de POC vers GitLab, en HTTPS).

---

## 1. Prérequis

### 1.1 Logiciels sur la machine de POC

Aucun logiciel à installer : le script n'utilise que Windows PowerShell 5.1. Les sources sont téléchargées en zip, générées par GitLab à la demande (**git n'est pas nécessaire**).

Le SDK .NET servira au build (ticket 3) :

```powershell
dotnet --list-sdks
```

Résultat attendu : une ligne `8.0.xxx`.

### 1.2 Jeton d'accès GitLab

Dans le projet GitLab : Settings > Access tokens > Add new token.

| Champ | Valeur |
|---|---|
| Nom | Par exemple `ci-dev-poc` |
| Rôle | **Developer** |
| Scopes | `read_api` (liste des MR) et `read_repository` (téléchargement des sources) |
| Expiration | Selon la règle de l'entreprise ; noter la date pour le renouveler |

Copier le jeton affiché : il ne sera plus montré.

Au ticket 3 (mise à jour du statut dans GitLab), il faudra un jeton avec le scope `api`.

### 1.3 Dossiers et fichier chiffré du jeton

**En étant connecté avec le compte qui fera tourner la tâche planifiée**, console PowerShell :

```powershell
New-Item -ItemType Directory -Path D:\ci\runner, D:\ci\build -Force
```

Résultat attendu : les deux dossiers sont listés.

```powershell
Read-Host "Jeton GitLab" -AsSecureString | Export-Clixml -Path D:\ci\runner\gitlab-token.xml
```

Coller le jeton à l'invite (il ne s'affiche pas), puis Entrée. Résultat attendu : aucun message, et le fichier `D:\ci\runner\gitlab-token.xml` existe.

Le fichier est chiffré par Windows (DPAPI) : **seul ce compte, sur cette machine**, peut le relire.

---

## 2. Configuration

En tête du script, section `>>> DEBUT CONFIGURATION` :

| Variable | À renseigner |
|---|---|
| `$GitLabUrl` | Adresse de GitLab, sans `/` final (ex. `https://gitlab.exemple.fr`) |
| `$GitLabProjectId` | Identifiant numérique du projet (affiché sur la page d'accueil du projet) |

L'adresse du dépôt est lue dans GitLab : rien d'autre à renseigner.

---

## 3. Premier lancement à la main

Avec le même compte, console PowerShell :

```powershell
cd D:\ci\runner
.\ci-dev.ps1
```

Résultats attendus :

- **aucune MR ouverte** : `Aucune MR à traiter (0 ouverte(s)).` ;
- **une MR ouverte** : téléchargement des sources (taille de l'archive affichée), remplacement du contenu de `D:\ci\build`, puis `MR !<numéro> : sources déposées dans D:\ci\build.` ;
- **relancé aussitôt** : `Aucune MR à traiter (1 ouverte(s)).` (le commit est déjà traité).

---

## 4. Lancement toutes les minutes

Console PowerShell **en administrateur**, avec le compte qui fera tourner la tâche. La fenêtre demande le mot de passe de ce compte : Windows le conserve chiffré, pour lancer la tâche même sans session ouverte, et sans fenêtre visible.

```powershell
$credential = Get-Credential "$env:USERDOMAIN\$env:USERNAME"
$action     = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\ci-dev.ps1" -WorkingDirectory "D:\ci\runner"
$trigger    = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 1)
$settings   = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 1)
Register-ScheduledTask -TaskName "Styx.CiDev" -Action $action -Trigger $trigger -Settings $settings -User $credential.UserName -Password $credential.GetNetworkCredential().Password
```

Résultat attendu : une ligne `Styx.CiDev` à l'état `Ready`. Sans durée de répétition, la tâche se répète indéfiniment (Windows Server 2016 et plus).

- Suspendre le runner : `Disable-ScheduledTask Styx.CiDev`
- Le reprendre : `Enable-ScheduledTask Styx.CiDev`
- Dernier résultat : `Get-ScheduledTaskInfo Styx.CiDev` (`LastTaskResult` : `0` = réussite, `1` = erreur)

---

## 5. Fichiers produits

| Emplacement | Contenu |
|---|---|
| `D:\ci\build\` | Sources du dernier commit traité |
| `D:\ci\runner\processed.txt` | Fichier « j'ai travaillé » : une ligne par commit traité (commit, numéro de MR, date) |
| `D:\ci\runner\logs\ci-dev-<date>.log` | Journal de chaque lancement qui a travaillé (aucun fichier quand il n'y a rien à faire) |

Pour faire retraiter une MR : supprimer la ligne de son commit dans `processed.txt`.

---

## 6. Limites de la v0

- **Un seul dossier de build** : si plusieurs MR sont à traiter au même lancement, elles sont traitées l'une après l'autre, et `D:\ci\build` contient la dernière. Sans conséquence tant que le build n'est pas branché (ticket 3), où chaque MR sera compilée juste après la récupération de ses sources.
- **Contenu de l'archive** : tous les fichiers du dépôt à ce commit, sans le dossier `.git`, ni les sous-modules, ni les fichiers Git LFS.
- **Chemins longs** : l'extraction du zip par Windows PowerShell 5.1 peut échouer sur des chemins de plus de 260 caractères.
- **En cas d'erreur**, le commit n'est pas noté : il sera retenté à la minute suivante (une erreur par minute dans les journaux tant qu'elle n'est pas corrigée).
- **Script non exécuté sur Windows** lors de sa rédaction : à valider avec le premier lancement à la main (§ 3).

---

## 7. Installation du script

Copier `ci-dev.ps1` (de ce dossier) dans `D:\ci\runner\`, puis renseigner la configuration (§ 2). Le fichier est enregistré en UTF-8 avec BOM : le garder ainsi en cas de modification, sinon les accents sont corrompus sous Windows PowerShell 5.1.
