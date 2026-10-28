# Module 08 — Intégration dans FETEAD

Ce dépôt ne contient que du **nouveau code**. Aucun fichier existant de FETEAD n'est modifié.
Les modifications de l'existant nécessaires pour activer le module sont listées ici,
pour être appliquées dans un second temps.

---

## 1. Nouveau code livré

| Emplacement | Contenu |
|-------------|---------|
| `FeteadApi.Infrastructure/Services/Monitoring/` | Options, registre des environnements, parseur de notifications, agrégateur, `BackgroundService` LISTEN, repository Dapper, service et fusion du snapshot, découpage temporel. |
| `FeteadApi.Presentation/Monitoring/` | `MonitoringHub`, `SignalRMonitoringPublisher`, `MonitoringController` (`/monitoring/*`), `MonitoringSetup` (extensions de branchement). |
| `frontend/pages/monitoring/` | `index.html`, `monitoring.css`, `monitoring.js`. |
| `tests/FeteadApi.Monitoring.Tests/` | Tests xUnit du code Infrastructure. |
| `tests/frontend/` | Tests Node du modèle de la page. |

Les chemins reprennent la structure de la solution (`FeteadApi.Infrastructure`, `FeteadApi.Presentation`,
dossier `frontend` trouvé par `FindFrontendPath`) : la copie se fait dossier par dossier.

---

## 2. Modifications de l'existant — non incluses

À appliquer dans cet ordre, chacune séparément.

### 2.1 Dépendances `FeteadApi.Infrastructure.csproj`

Vérifier la présence de (versions à aligner sur la solution) :

```xml
<PackageReference Include="Dapper" />
<PackageReference Include="Npgsql" />
<PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
<PackageReference Include="Microsoft.Extensions.Options" />
```

Aucun paquet n'est nécessaire côté Presentation : SignalR serveur fait partie d'ASP.NET Core.

### 2.2 `Program.cs`

```csharp
using FeteadApi.Presentation.Monitoring;

// avec les autres services
builder.Services.AddStyxMonitoring(builder.Configuration);

// après app.MapControllers();
app.MapStyxMonitoring();
```

⚠️ Tant que `AddStyxMonitoring` n'est pas appelé, `MonitoringController` est quand même découvert
par `AddControllers()` : un appel à `/monitoring/*` échouerait (dépendances non enregistrées).
Il n'y a aucun impact sur les autres endpoints.

### 2.3 `appsettings.json`

```json
"Monitoring": {
  "Environments": [
    { "Code": "LOCAL", "ConnectionStringVariable": "StyxConnectionLocal" },
    { "Code": "POC",   "ConnectionStringVariable": "StyxConnectionPoc" },
    { "Code": "INT",   "ConnectionStringVariable": "StyxConnectionInt" }
  ],
  "LatestJobsLimit": 50,
  "ReconnectDelaySeconds": 5,
  "MaxRangeDays": 92,
  "TaskJobCacheSize": 10000
}
```

Les chaînes restent dans les variables d'environnement `StyxConnection*`, lues directement
(`appsettings.json` est ré-ajouté après les variables dans `Program.cs` et passerait devant).
Une variable absente rend l'environnement `Unavailable` sans empêcher le démarrage.

### 2.4 Carte du portail (`frontend/index.html`)

À ajouter dans `.apps-grid`, avant la carte « À venir » (qui porte aujourd'hui aussi le numéro 07) :

```html
<a class="app-card" href="/pages/monitoring/">
  <span class="app-num">08</span>
  <div class="app-icon"><svg viewBox="0 0 24 24"><polyline points="22 12 18 12 15 21 9 3 6 12 2 12"/></svg></div>
  <div class="app-name">STYX Monitoring</div>
  <div class="app-desc">Supervision temps réel des DataJobs et de leurs tâches, multi-environnements.</div>
  <div class="app-arrow">Ouvrir →</div>
</a>
```

### 2.5 Base de données — extension du trigger (décision D5)

Sans cette étape, le module fonctionne, mais la **progression** n'est mise à jour qu'aux
changements d'état. Vérifier d'abord qui écoute `scheduler_events` (point ouvert n° 1 du cadrage).

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
                'name',          NEW.name,
                'state',         NEW.state,
                'previousState', OLD.state,
                'progress',      NEW.progress,
                'at',            now()
            )::text);
    END IF;
    RETURN NEW;
END;
$$;

-- Le trigger existant est créé via IF NOT EXISTS : changer ses colonnes impose DROP + CREATE.
DROP TRIGGER IF EXISTS trg_notify_data_job_state ON public.data_job;
CREATE TRIGGER trg_notify_data_job_state
    AFTER UPDATE OF state, progress ON public.data_job
    FOR EACH ROW EXECUTE FUNCTION public.notify_data_job_state();
```

### 2.6 Base de données — index (recommandés)

Utilisés par le comptage des tâches et la résolution tâche → job :

```sql
CREATE INDEX IF NOT EXISTS ix_data_task_job_id      ON public.data_task(job_id);
CREATE INDEX IF NOT EXISTS ix_data_task_external_id ON public.data_task(external_id);
```

---

## 3. Vérifications après intégration

1. Démarrage : log `[Monitoring:LOCAL] LISTEN actif` pour chaque environnement configuré.
2. `GET /monitoring/environments` → statuts `Listening`.
3. `GET /monitoring/snapshot?envs=LOCAL` → KPI, séries, jobs.
4. Page `/pages/monitoring/` : pastille « En direct », puis un `UPDATE public.data_job SET state = …`
   sur un job de test doit apparaître dans la timeline sans rechargement.
5. Onglet Réseau du navigateur : aucune requête périodique vers `/monitoring/*`.
6. Couper une base (ou son accès réseau) : l'environnement passe en `Reconnecting`, les autres continuent ;
   au retour, la page recharge automatiquement son snapshot.
