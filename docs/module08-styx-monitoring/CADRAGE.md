# Module 08 — STYX Monitoring · Cadrage technique

> Statut : **nouveau code livré dans ce dépôt, non intégré** (voir `INTEGRATION.md`).
> Ce document complète la spec fonctionnelle « Module 08 — STYX Monitoring » et fixe la
> manière de l'implémenter dans l'existant. Toute déviation doit être validée avant développement.

---

## 1. Décisions validées (28/10/2026)

| # | Sujet | Décision |
|---|-------|----------|
| D1 | Hébergement backend | Dans l'**API FETEAD** (`FeteadApi`), qui sert déjà le frontend. |
| D2 | Environnements | Le serveur écoute **tous** les environnements configurés ; chaque navigateur **filtre** l'affichage. Liste d'environnements lue en configuration (extensible). |
| D3 | Graphique | **SVG fait main**, aucune librairie. |
| D4 | Périmètre | **Plage temporelle paramétrable** (étude d'une situation passée possible). |
| D5 | Progression temps réel | **Étendre le trigger** `notify_data_job_state` à `progress`. |
| D6 | Historique du graphique | **Le plus simple** : reconstitution à partir des dates existantes de `data_job`, **sans table d'historique**. |

Rappels de la spec, inchangés : HTML/CSS/JS/jQuery/SignalR, Design System FETEAD strict,
aucun framework front, **aucun rafraîchissement périodique navigateur**.

---

## 2. Existant vérifié

Sources : photos du schéma et du front (28/10), `Program.cs` de `FeteadApi.Presentation`,
repo `sgdesign` (version historique — **son SQL ne reflète plus le schéma actuel**,
ex. `data_task.data_job_id`/`task_state` dans le repo vs `job_id`/`state` en base).

### Base de données

| Élément | Constat |
|---------|---------|
| `public.data_job` | `id, name, state, progress (0–100), submit_date_time, create_date_time, start_date_time, change_date_time` (TIMESTAMPTZ). **Pas d'`end_date_time`, pas d'`updated_at`, pas d'historique d'états.** |
| `public.data_task` | `id, job_id → data_job, task_id, external_id, name, state, *_date_time` (TIMESTAMP **sans** fuseau). **Aucun trigger, aucun index visible** sur `job_id` ni `external_id`. |
| `scheduler_events` | `kind:'job'` → `{jobId, state, progress, at}` (trigger `trg_notify_data_job_state`, `AFTER UPDATE OF state`, seulement si l'état change). `kind:'task'` → `{taskId, externalId, state, runnerId, reason, at}` (`scheduler.notify_task_state_change`). |
| `taskflow_events` | `{taskId, externalId, state, serverId, reason, at}` (`taskflow.notify_task_state_change`). |

### API FETEAD (`Program.cs`)

- Minimal hosting, controllers, Dapper (`MatchNamesWithUnderscores = true`), couches `Presentation` / `Infrastructure`.
- Précédent à suivre : **LocalDb Control** → `FeteadApi.Infrastructure.Services.LocalDb`, options `Configure<PostgresControlOptions>(GetSection("LocalDbControl"))`, service singleton.
- Le frontend est servi **par l'API elle-même** (`FindFrontendPath` → `wwwroot` ou dossier `frontend`).
- **SignalR n'est pas encore branché** (ni `AddSignalR`, ni `MapHub`).
- Restriction loopback appliquée **uniquement** à `/localdb-control`.

### Front existant réutilisable (TaskFlow)

`conn-dot` (live / offline / reconnecting), `stat-card[data-state]`, `card`, `table-wrap`,
`timeline`, `sdot()`, `esc()`, `fmt()`, `withAutomaticReconnect`.
⚠️ `taskflow.js` contient `setInterval(loadAll, 30000)` : **à ne pas reproduire**.

---

## 3. Architecture cible

```text
PostgreSQL LOCAL ─┐                              ┌─ REST  /monitoring/*        (snapshot, plage)
PostgreSQL POC   ─┼─ LISTEN scheduler_events ──► │
PostgreSQL INT   ─┘        taskflow_events       │  MonitoringListenerService (1 connexion LISTEN / env)
                                                 │        │
                                                 │        ▼
                                                 │  (retrouve le job d'une tâche via data_task.external_id)
                                                 │        │
                                                 └─ SignalR /monitoringHub ──► Navigateur (filtre par env)
```

Deux chemins, volontairement séparés :

- **Snapshot (REST)** : état sur une plage, au chargement, au changement de plage/env, et après **toute reconnexion**.
- **Delta (SignalR)** : appliqué uniquement en **mode direct** (plage se terminant à « maintenant »).

---

## 4. Modification de base (D5)

Seule modification de schéma du module. Le trigger existant est créé via `IF NOT EXISTS` :
changer sa liste de colonnes impose **DROP + CREATE**.

```sql
CREATE OR REPLACE FUNCTION public.notify_data_job_state()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE'
       AND (OLD.state    IS DISTINCT FROM NEW.state
         OR OLD.progress IS DISTINCT FROM NEW.progress)
    THEN
        PERFORM pg_notify(
            'scheduler_events',
            json_build_object(
                'kind',          'job',
                'jobId',         NEW.id,
                'name',          NEW.name,        -- ajout : évite une requête par événement
                'state',         NEW.state,
                'previousState', OLD.state,       -- ajout : permet d'ajuster les compteurs
                'progress',      NEW.progress,
                'at',            now()
            )::text);
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_notify_data_job_state ON public.data_job;
CREATE TRIGGER trg_notify_data_job_state
    AFTER UPDATE OF state, progress ON public.data_job
    FOR EACH ROW EXECUTE FUNCTION public.notify_data_job_state();
```

Les champs ajoutés sont **additifs** : un consommateur existant qui lit `jobId/state` n'est pas cassé.
⚠️ En revanche, un consommateur qui traite chaque événement `job` comme un changement d'état
recevra désormais aussi les mises à jour de progression → **à vérifier avant déploiement** (cf. §10).

Index recommandés (non bloquants, à valider) :

```sql
CREATE INDEX IF NOT EXISTS ix_data_task_job_id      ON public.data_task(job_id);
CREATE INDEX IF NOT EXISTS ix_data_task_external_id ON public.data_task(external_id);
```

---

## 5. Backend (API FETEAD)

### 5.1 Configuration

La spec impose les variables `StyxConnectionLocal / Poc / Int`. Pour rester extensible (D2),
la **liste** des environnements est en configuration et chaque entrée **référence** sa variable :

```json
"Monitoring": {
  "Environments": [
    { "Code": "LOCAL", "ConnectionStringVariable": "StyxConnectionLocal" },
    { "Code": "POC",   "ConnectionStringVariable": "StyxConnectionPoc" },
    { "Code": "INT",   "ConnectionStringVariable": "StyxConnectionInt" }
  ],
  "LatestJobsLimit": 50
}
```

Ajouter un environnement = une ligne de configuration + une variable, sans code.

⚠️ **Piège identifié dans `Program.cs`** : `AddJsonFile("appsettings.json")` est ré-ajouté
**après** les sources par défaut, il passe donc **devant** les variables d'environnement.
Les chaînes ne doivent pas être lues via une clé que `appsettings.json` pourrait définir :
lecture explicite par `Environment.GetEnvironmentVariable(ConnectionStringVariable)`.
Une variable absente → environnement marqué `Unavailable`, l'API démarre quand même.

### 5.2 Composants

Principe directeur : **lisible par un développeur junior**. On reprend les schémas déjà connus
de l'équipe (`TaskFlowNotificationListener`, LocalDb), sans interface ni abstraction supplémentaire.

| Fichier | Couche | Rôle |
|---------|--------|------|
| `MonitoringOptions` | Infrastructure | Section `Monitoring` de `appsettings.json`. |
| `MonitoringModels` | Infrastructure | Classes de données (notification, messages SignalR, snapshot). Aucune logique. |
| `MonitoringEnvironments` | Infrastructure | Liste des environnements, chaîne de connexion et statut d'écoute. Singleton partagé. |
| `MonitoringRepository` | Infrastructure | Requêtes Dapper en lecture seule (§7). |
| `MonitoringSnapshotService` | Infrastructure | Interroge chaque environnement et regroupe les résultats. |
| `MonitoringListenerService` | Presentation | 1 boucle LISTEN par environnement, reconnexion après 5 s, envoi SignalR. Même schéma que `TaskFlowNotificationListener`. |
| `MonitoringHub` | Presentation | Hub SignalR, sans méthode : le navigateur écoute seulement. |
| `MonitoringController` | Presentation | `GET /monitoring/environments` et `GET /monitoring/snapshot`. |

### 5.3 Câblage `Program.cs`

Enregistrements écrits en clair, comme pour LocalDb (détail dans `INTEGRATION.md`) :

```csharp
builder.Services.Configure<MonitoringOptions>(builder.Configuration.GetSection("Monitoring"));
builder.Services.AddSingleton<MonitoringEnvironments>();
builder.Services.AddSingleton<MonitoringRepository>();
builder.Services.AddSingleton<MonitoringSnapshotService>();
builder.Services.AddHostedService<MonitoringListenerService>();
builder.Services.AddSignalR();
// …
app.MapControllers();
app.MapHub<MonitoringHub>("/monitoringHub");
```

Le frontend étant servi par l'API, le client utilise une **URL relative** (`/monitoringHub`),
contrairement à TaskFlow (`API_HOST` codé en dur).

### 5.4 Contrats

REST :

| Endpoint | Rôle |
|----------|------|
| `GET /monitoring/environments` | `[{ code, status }]` — `Listening`, `Reconnecting`, `Unavailable`. |
| `GET /monitoring/snapshot?envs=LOCAL,POC&from=…&to=…&bucket=…` | `{ generatedAt, kpi, series, jobs }` sur la plage (§7). |

SignalR (serveur → client) :

| Événement | Payload |
|-----------|---------|
| `JobChanged` | `{ env, jobId, name, state, previousState, progress, at, receivedAt }` |
| `TaskChanged` | `{ env, source: 'taskflow' \| 'scheduler', taskId, externalId, jobId, state, reason, at, receivedAt }` |
| `EnvironmentStatusChanged` | `{ env, status, since }` |

Diffusion à tous les clients ; le filtrage par environnement est fait côté navigateur (D2).
`receivedAt` (horloge serveur) sert d'horodatage d'affichage (cf. §8, fuseaux).

---

## 6. Front (`/pages/monitoring/`)

Fichiers : `index.html`, `monitoring.css`, `monitoring.js`, plus la carte **08 — STYX MONITORING**
dans le portail. Structure de page identique à TaskFlow (`header`, `page-top`, `main`, `footer`,
`styles.css` + `app.js` communs). Les composants TaskFlow sont repris en liant
`../taskflow/taskflow.css` (pas de copie) ; `monitoring.css` ne contient que les ajouts.

### Bloc 1 — Pilotage

| Élément | Composant réutilisé |
|---------|--------------------|
| Statut de connexion (hub) | `#conn-status` / `conn-dot` de TaskFlow, tel quel. |
| Statut par environnement | Pastille `conn-dot` à côté de chaque bascule d'env. |
| Sélection des environnements | **`mode-tab` en bascule** (le Design System n'a pas de case à cocher). Choix mémorisé en `localStorage` (try/catch). |
| Plage temporelle | `mode-tab` : `1 h · 24 h · 7 j · Personnalisée` ; personnalisée = 2 `form-input` `datetime-local`. |
| KPI | `#stat-grid` / `stat-card[data-state]` (5 états), tels quels. |

### Bloc 2 — Supervision

| Élément | Rendu |
|---------|-------|
| Graphique d'évolution | `card` contenant un `<svg>` généré en JS : 5 séries (couleurs `--yellow/--cyan/--green/--orange/--gray`, déjà associées aux états), axe temps, légende en `stat-label`. |
| Tableau DataJobs | `card` + `table-wrap` : `ENV · ID · NAME · STATE (sdot) · PROGRESS · TASKS · UPDATED`. |
| Timeline temps réel | Composant `timeline` de TaskFlow ; entrées `HH:mm:ss [ENV] Job 1024 Running`, **plafonnée** (ex. 200 entrées). |

### Modes

- **Direct** (plage finissant à « maintenant ») : snapshot au chargement, puis deltas SignalR.
- **Historique** (plage passée) : snapshot seul, deltas ignorés, mention visible « Vue figée ».

Le seul déclencheur de rechargement est une action utilisateur ou une reconnexion. **Aucun `setInterval` de données.**
(L'horloge du footer, purement locale, reste autorisée.)

---

## 7. Règles de calcul

Notation : `activité(job) = COALESCE(change_date_time, start_date_time, submit_date_time, create_date_time)`.

| Indicateur | Règle |
|------------|-------|
| **Jobs du périmètre** | Jobs des environnements sélectionnés avec `activité(job)` dans `[from, to]`. |
| **KPI** | `COUNT(*) GROUP BY data_job.state` sur le périmètre, pour les 5 états suivis (référence = `data_job.state`, conforme spec). |
| **Nombre de tâches** | `COUNT(data_task.id)` par `data_task.job_id` (conforme spec). |
| **Progression** | `data_job.progress` (conforme spec). |
| **UPDATED** | `activité(job)`. |
| **Graphique (D6)** | **Flux par intervalle**, pas un stock : *Submitted* = jobs dont `submit_date_time` tombe dans l'intervalle ; *Running* = `start_date_time` ; *Finished / Failed / Canceled* = `change_date_time` des jobs actuellement dans cet état. |
| **Intervalle du graphique** | Plus petit pas « rond » donnant au plus 60 points : 1 h → 1 min · 24 h → 30 min · 7 j → 3 h · au-delà de 60 j → multiple de jours. |

Mise à jour en direct :

- `JobChanged` avec `previousState ≠ state` : décrément de `previousState`, incrément de `state` ; ligne du tableau insérée ou mise à jour ; point ajouté dans l'intervalle courant.
- `JobChanged` avec seulement `progress` : mise à jour de la colonne PROGRESS, rien d'autre (pas d'entrée timeline).
- `TaskChanged` : entrée timeline **uniquement pour les tâches `Failed`**, pour ne pas noyer les événements de jobs.

**Limites assumées de D6** : relances et états intermédiaires invisibles dans le graphique ;
en vue historique, KPI et tableau montrent l'état **actuel** des jobs, pas leur état à la date de fin de plage ;
l'exactitude dépend de la mise à jour de `change_date_time` par **tous** les chemins
d'écriture (Light et Grid) — non vérifiable avec les éléments disponibles.
Si ce n'est pas le cas, la correction la plus simple est de renseigner `change_date_time`
dans un trigger BEFORE (nouvelle décision à prendre).

---

## 8. Cas limites et robustesse

| Risque | Traitement |
|--------|-----------|
| NOTIFY perdus pendant une coupure DB | À la reconnexion LISTEN : `EnvironmentStatusChanged` → le client recharge le snapshot de cet env. |
| Coupure SignalR | `withAutomaticReconnect` ; `onreconnected` → snapshot complet. |
| Réponse de snapshot périmée (plage changée entre-temps) | Jeton de requête côté client ; une réponse obsolète est ignorée. |
| Événement reçu pendant le chargement du snapshot | Mis en file, rejoué après le snapshot si `receivedAt > generatedAt`. |
| Fuseaux (`TIMESTAMP` côté tâches vs `TIMESTAMPTZ` côté jobs) | Affichage et tri sur `receivedAt` serveur ; dates de la base renvoyées en ISO 8601 avec offset. |
| Volume d'événements tâches | Timeline plafonnée ; aucune requête par événement côté client. |
| Plusieurs onglets / postes | Indépendants : chaque client a son snapshot ; le serveur n'a aucun état par client. |
| Environnement injoignable | Marqué `Unavailable`, exclu des agrégats, signalé visuellement ; les autres continuent. |
| `externalId` introuvable | Événement diffusé avec `jobId: null`, journalisé en Warning. |
| Secrets | Chaînes de connexion jamais journalisées ni renvoyées par l'API. |

---

## 9. Tests

À adapter à l'infrastructure de test existante de `FeteadApi` (non vue à ce stade).

- Lecture des 3 formes de notification (job, tâche Scheduler, tâche TaskFlow).
- Taille des intervalles du graphique et regroupement des environnements (`MonitoringSnapshotService`).
- Environnements : variable absente → `Unavailable` sans erreur au démarrage ; code en double refusé.
- Non testé unitairement : `MonitoringListenerService` et les requêtes SQL (base réelle nécessaire) → vérifications manuelles de `INTEGRATION.md` § 3.
- Front (manuel) : bascule d'env, mode historique figé, coupure / reprise du hub, absence de requêtes réseau périodiques (onglet Réseau du navigateur).

---

## 10. Points ouverts et risques

1. **Consommateurs de `scheduler_events`** : identifier qui écoute ce canal (absent du repo historique) et vérifier qu'ils tolèrent les événements de progression (D5).
2. **Espace des `external_id`** : TaskFlow et Scheduler partagent-ils le même espace d'identifiants côté `data_task` ? Sinon, la résolution tâche → job est ambiguë.
3. **Fiabilité de `change_date_time`** sur les chemins Light et Grid (conditionne D6).
4. **États hors KPI** : `Queued`, `Validating`, `Configuring`, `Canceling`… ne sont rattachés à aucune carte (spec : 5 états). À confirmer.
5. **Exposition** : le module affiche des données POC/INT. La restriction loopback n'existe que pour `/localdb-control`, alors que la CORS autorise une origine distante. Faut-il protéger `/monitoring` et `/monitoringHub` de la même façon ?
6. **Accès réseau** : chaque instance de FETEAD ouvre une connexion LISTEN par environnement ; le poste qui l'exécute doit joindre les bases POC/INT et détenir leurs identifiants.
7. **Code source** : le frontend et l'API FETEAD réels ne sont pas dans `sgdesign`. L'implémentation se fera dans le dépôt FETEAD.
