# got — Spécification fonctionnelle

> Statut : **spécification, aucun code écrit**. Décisions validées le 12/11/2026.
> Les éléments marqués **[à valider]** sont des propositions, pas des décisions.
> Toute déviation doit être validée avant développement.

---

## 1. Objectif

`got` est une application du dashboard FETEAD (nouvelle carte du portail) qui gère le versioning de la
solution FETEAD entre :

- le **local** : la copie de la solution sur le poste de chaque développeur ;
- le **share** : la copie de référence, sur un partage réseau, commune à toute l'équipe.

Elle ne reprend de git que la synchronisation : savoir ce qui a changé, récupérer, publier, résoudre
les conflits. Plusieurs développeurs publient sur le même share.

### Hors périmètre

- Branches, historique des versions, retour à une version antérieure (hors sauvegardes, §4.4).
- Fusion ligne à ligne d'un fichier.
- Détection en temps réel des modifications.
- Mode hors connexion : si le share est inaccessible, `got` l'affiche et ne propose aucune action.

---

## 2. Vocabulaire

| Terme | Définition |
|-------|------------|
| **Racine locale** | Dossier de la solution FETEAD sur le poste. |
| **Share** | Dossier de la solution sur le partage réseau. Son chemin est lu dans l'appsettings. |
| **Manifeste** | `<share>/.got/manifest.json` : l'état publié du share (§4.1). |
| **Base** | `<local>/.got/base.json` : copie du manifeste au moment de la dernière synchronisation de ce poste. |
| **Synchroniser** | Récupérer (F2) ou fusionner (F3) : la base devient égale au manifeste. |
| **Fichier suivi** | Tout fichier de la racine, hors fichiers ignorés et hors dossier `.got/`. |

---

## 3. Décisions validées (12/11/2026)

| # | Sujet | Décision |
|---|-------|----------|
| D1 | Intégration | Nouvelle application du dashboard FETEAD, nommée `got`. |
| D2 | Calcul de l'état | Au lancement de l'application et sur le bouton **Rafraîchir**. Pas de temps réel. |
| D3 | Share | Chemin lu dans l'appsettings. Plusieurs développeurs y publient. |
| D4 | Local | Le poste de chaque développeur. |
| D5 | Référence commune | Un **manifeste** sur le share (version, hash de chaque fichier, auteur et date de publication) et une **base** par poste. |
| D6 | `.gotignore` | Même sémantique que `.gitignore`. Fichier suivi comme les autres. C'est la version **locale** qui s'applique. |
| D7 | Récupérer | Refusé s'il existe des modifications locales. |
| D8 | Publier | Tous les fichiers modifiés hors fichiers ignorés, ajouts et suppressions compris. Refusé si le share a bougé depuis la dernière synchronisation. |
| D9 | Concurrence | Verrou exclusif sur le share pendant une publication, avec expiration en cas de crash. |
| D10 | Conflits | Résolus fichier par fichier : garder la version locale ou la version distante. Pas de fusion ligne à ligne. |
| D11 | Sauvegarde | Copie horodatée de tout fichier écrasé ou supprimé, en local (récupération) comme sur le share (publication). |

---

## 4. Données

### 4.1 Manifeste (`<share>/.got/manifest.json`)

```json
{
  "version": 42,
  "publishedBy": "jdupont",
  "publishedAt": "2026-11-12T14:05:00+01:00",
  "files": {
    "frontend/index.html": "sha256:9f2c…",
    "FeteadApi.Presentation/Program.cs": "sha256:41ab…"
  }
}
```

- `version` : entier, incrémenté de 1 à chaque publication. Absence de manifeste = share jamais initialisé.
- Chemins relatifs à la racine, séparateur `/`.
- Hash : SHA-256 du contenu binaire du fichier.
- `publishedBy` : nom de l'utilisateur Windows (`Environment.UserName`).

### 4.2 Base (`<local>/.got/base.json`)

Même format que le manifeste. Absente au premier lancement sur un poste : elle est alors considérée comme
vide (§5.2).

### 4.3 Verrou (`<share>/.got/lock`)

```json
{ "owner": "jdupont", "machine": "PC-1234", "since": "2026-11-12T14:04:58+01:00" }
```

Créé de façon exclusive (`FileMode.CreateNew` : la création échoue si le fichier existe déjà), supprimé
en fin de publication. Considéré comme expiré au-delà de `LockTimeoutMinutes` (appsettings).

### 4.4 Sauvegardes

- Local : `<local>/.got/backups/<aaaaMMjj-HHmmss>/<chemin du fichier>`.
- Share : `<share>/.got/backups/<aaaaMMjj-HHmmss>-<utilisateur>/<chemin du fichier>`.
- Durée de conservation : **[à valider]**, voir §10.

### 4.5 Configuration (appsettings)

```json
"Got": {
  "SharePath": "\\\\serveur\\partage\\FETEAD",
  "LocalPath": "C:\\Dev\\FETEAD",
  "LockTimeoutMinutes": 10
}
```

`LocalPath` et la valeur par défaut de `LockTimeoutMinutes` sont **[à valider]** (§10).

---

## 5. État d'un fichier

### 5.1 Règle de calcul

Pour chaque fichier suivi présent dans le local, la base ou le manifeste, on compare trois hash :
**L** (local), **B** (base), **R** (manifeste distant). « absent » est une valeur comme une autre.

| Cas | Condition | Statut affiché |
|-----|-----------|----------------|
| E1 | L = B = R | À jour |
| E2 | L ≠ B et R = B | Modifié localement (*ajouté* si B absent, *supprimé* si L absent) |
| E3 | L = B et R ≠ B | Modifié à distance (*ajouté* si B absent, *supprimé* si R absent) |
| E4 | L ≠ B, R ≠ B et L = R | À jour (même modification des deux côtés) |
| E5 | L ≠ B, R ≠ B et L ≠ R | **Conflit** |

Les fichiers ignorés n'ont pas de statut : ils sont affichés grisés dans l'arborescence et ne sont jamais
lus, copiés ni supprimés par `got`.

### 5.2 Premier lancement sur un poste

La base absente est traitée comme vide. Les règles ci-dessus donnent alors : fichier identique des deux
côtés → à jour (E4) ; différent → conflit (E5) ; présent d'un seul côté → ajouté localement ou à distance.

### 5.3 Indicateurs globaux

- **Modifications locales** : au moins un fichier en E2 ou E5.
- **Share en avance** : `manifest.version ≠ base.version`.

---

## 6. Écran

- **En-tête** : chemin du share, version distante, version de la base, auteur et date de la dernière
  publication, présence d'un verrou (auteur, depuis quand).
- **Arborescence** de la solution, avec un badge de statut par fichier et par dossier (un dossier porte le
  statut le plus important de son contenu : conflit > modifié > à jour). Fichiers ignorés grisés.
- **Listing des fichiers modifiés**, en trois groupes : conflits, modifiés localement, modifiés à distance.
- **Actions** : Rafraîchir, Récupérer, Fusionner, Publier. Une action non autorisée est désactivée, avec la
  raison affichée.
- **[à valider]** Diff côte à côte d'un fichier texte (local / distant). Fichier binaire : « fichiers
  différents », sans diff.
- **[à valider]** Annuler une modification locale (F5).
- Design System FETEAD strict. Publier est l'action irréversible : bouton rouge plein.

---

## 7. Flux

### F1 — Calculer l'état

Au lancement et sur **Rafraîchir** : lire le `.gotignore` local, hacher les fichiers suivis locaux, lire la
base et le manifeste, appliquer §5. Le share n'est pas re-haché : on lui fait confiance via le manifeste (H1).

### F2 — Récupérer

Préconditions : share accessible, aucune modification locale, share en avance.

1. Sauvegarder en local chaque fichier qui va être écrasé ou supprimé.
2. Copier depuis le share les fichiers en E3 ajoutés ou modifiés ; supprimer en local ceux en E3 supprimés.
3. Base := manifeste.

### F3 — Fusionner (« accepter les modifications et merger »)

Préconditions : share accessible, modifications locales **et** share en avance, chaque conflit a reçu un
choix (garder local / garder distant).

1. Sauvegarder en local chaque fichier qui va être écrasé ou supprimé.
2. Appliquer les fichiers en E3 comme dans F2.
3. Conflits « garder distant » : remplacer le fichier local par celui du share.
   Conflits « garder local » : ne rien toucher.
4. Base := manifeste.

Après F3, les modifications locales conservées (E2 et conflits « garder local ») restent à publier.

### F4 — Publier

Préconditions : share accessible, modifications locales, share **pas** en avance, aucun conflit.

1. Créer le verrou. S'il existe et n'est pas expiré : refus, en affichant son auteur et son heure.
   S'il est expiré : **[à valider]** proposer de le reprendre, avec confirmation.
2. Relire le manifeste sous verrou. Si sa version a changé : libérer le verrou, refus (« le share a bougé,
   fusionnez d'abord »).
3. Sauvegarder sur le share chaque fichier qui va être écrasé ou supprimé.
4. Copier vers le share les fichiers en E2 ajoutés ou modifiés ; supprimer du share ceux en E2 supprimés.
5. Écrire le nouveau manifeste (`version + 1`, auteur, date, hash) dans un fichier temporaire puis le
   renommer, pour qu'un lecteur ne lise jamais un manifeste à moitié écrit.
6. Supprimer le verrou. Base := nouveau manifeste.

Le manifeste est écrit en dernier : tant qu'il n'est pas remplacé, les autres postes ne voient pas la
publication.

### F5 — Annuler une modification locale **[à valider]**

Fichier en E2 uniquement. Sauvegarder la version locale, puis : modifié ou supprimé → recopier la version
du share ; ajouté → supprimer le fichier local.

### Initialisation du share

Si le manifeste est absent, la seule action proposée est **Initialiser le share** : une publication (F4)
de tous les fichiers suivis locaux, qui crée le manifeste en version 1. **[à valider]** Si le share contient
déjà des fichiers, ils sont écrasés après sauvegarde.

---

## 8. Règles (base des scénarios de recette)

| # | Règle |
|---|-------|
| R01 | L'état est recalculé au lancement et sur Rafraîchir, jamais en tâche de fond. |
| R02 | Un fichier ignoré par le `.gotignore` local n'est jamais lu, copié, supprimé ni publié. |
| R03 | Le dossier `.got/` est toujours ignoré, même absent du `.gotignore`. |
| R04 | Le statut d'un fichier suit le tableau §5.1. |
| R05 | Récupérer est refusé s'il existe au moins une modification locale. |
| R06 | Fusionner est refusé tant qu'un conflit n'a pas reçu de choix. |
| R07 | Publier est refusé si le share est en avance sur la base. |
| R08 | Publier est refusé s'il existe un conflit. |
| R09 | Publier est refusé si un verrou non expiré existe ; le refus indique son auteur et son heure. |
| R10 | Publier relit le manifeste sous verrou et s'arrête si sa version a changé. |
| R11 | Une publication incrémente la version du manifeste de 1 et y inscrit son auteur et sa date. |
| R12 | Le manifeste est écrit en dernier, de façon atomique (fichier temporaire puis renommage). |
| R13 | Tout fichier écrasé ou supprimé par `got` est d'abord sauvegardé (local ou share). |
| R14 | Après Récupérer, Fusionner ou Publier, la base est égale au manifeste. |
| R15 | Si le share est inaccessible, aucune action n'est proposée et la raison est affichée. |
| R16 | Une publication supprime du share les fichiers supprimés localement. |

---

## 9. Intégration dans FETEAD (à détailler au cadrage technique)

Même découpage que le Module 08 : interface dans `Domain.Interfaces.Got`, implémentation dans
`Infrastructure.Services.Got`, contrôleur `GotController` dans `Presentation`, options
`Configure<GotOptions>(GetSection("Got"))`, page `frontend/pages/got/`, nouvelle carte dans le portail
(numéro à définir). Aucune base de données : tout repose sur le système de fichiers.

---

## 10. Hypothèses et points ouverts

| # | Sujet | Proposition |
|---|-------|-------------|
| H1 | Le share n'est modifié que par `got`. Une copie faite à la main sur le share n'est pas détectée. | À confirmer avec l'équipe. |
| P1 | **FETEAD se met à jour lui-même.** Si la solution synchronisée contient les binaires de l'API en cours d'exécution, Windows refuse de les écraser. | Vérifier ce que contient la « solution FETEAD » (sources ou binaires). Point bloquant à lever en premier. |
| P2 | Racine locale : configurée (`LocalPath`) ou déduite de l'emplacement de l'API. | Configurée, plus explicite. |
| P3 | Casse des chemins : Windows ne la distingue pas, le manifeste si. | Comparer sans tenir compte de la casse. |
| P4 | Temps de hachage au lancement si la solution est volumineuse. | Tout hacher pour le MVP ; cache (taille + date de modification) seulement si c'est trop lent. |
| P5 | Durée de conservation des sauvegardes. | Garder les 10 dernières par côté. |
| P6 | Expiration du verrou. | 10 minutes ; reprise d'un verrou expiré avec confirmation. |
| P7 | Fichier ouvert ou verrouillé pendant une récupération. | Arrêt avant toute écriture, avec la liste des fichiers en cause. |
| P8 | Bibliothèque de lecture du `.gotignore` (ex. `MAB.DotIgnore`, `Ignore`). | Vérifier maintenance, licence et disponibilité sur l'Artifactory. |
| P9 | Diff (§6) et annulation (F5). | Proposés, à valider. |
| P10 | Le fichier appsettings qui contient le chemin du share est propre à chaque poste. | L'ajouter au `.gotignore`. |
| P11 | Coupure réseau pendant une publication : fichiers copiés mais manifeste inchangé. | Le publieur relance la publication ; les sauvegardes permettent de revenir en arrière. |
