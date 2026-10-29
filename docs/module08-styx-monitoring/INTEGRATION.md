# Module 08 — Intégration dans FETEAD

Ce dépôt contient du **nouveau code**, plus le portail `frontend/index.html` (ajout de la carte 08, voir § 2.5).
Les modifications de l'existant nécessaires pour activer le module sont listées ici,
pour être appliquées dans un second temps.

---

## 1. Nouveau code livré

| Emplacement | Contenu |
|-------------|---------|
| `FeteadApi.Domain/Interfaces/Monitoring/` | `IMonitoringRepository`, `IMonitoringSnapshotService`. |
| `FeteadApi.Domain/Models/Monitoring/` | `MonitoringModels` (classes de données, aucune logique). |
| `FeteadApi.Infrastructure/Repositories/Monitoring/` | `MonitoringRepository` (requêtes Dapper). |
| `FeteadApi.Infrastructure/Services/Monitoring/` | `MonitoringOptions`, `MonitoringEnvironments`, `MonitoringSnapshotService`. |
| `FeteadApi.Presentation/Monitoring/` | `MonitoringListenerService` (écoute PostgreSQL), `MonitoringHub`, `MonitoringController` (`/monitoring/*`). |
| `frontend/pages/monitoring/` | `index.html`, `monitoring.css`, `monitoring.js`. |
| `frontend/index.html` | Portail avec la carte 08 (seul fichier existant modifié, à la demande ; voir § 2.5). |
| `tests/FeteadApi.Monitoring.Tests/` | Tests xUnit du code Infrastructure. |
| `tests/frontend/` | Tests Node du modèle de la page. |

Les chemins et namespaces reprennent la structure de la solution (`Domain.Interfaces.<Module>`,
`Infrastructure.Repositories.<Module>`, `Infrastructure.Services.<Module>`, dossier `frontend`) :
la copie se fait dossier par dossier.

⚠️ `FeteadApi.Domain.Models.Monitoring` est une supposition : si les modèles des autres modules
sont rangés ailleurs dans `FeteadApi.Domain`, déplacer `MonitoringModels.cs` au même endroit.

---

## 2. Modifications de l'existant — non incluses

À appliquer dans cet ordre, chacune séparément.

### 2.1 Dépendances `FeteadApi.Infrastructure.csproj`

Vérifier la présence de (versions à aligner sur la solution) :

```xml
<PackageReference Include="Dapper" />
<PackageReference Include="Npgsql" />
<PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" />
```

Aucun paquet n'est nécessaire côté Presentation : SignalR serveur fait partie d'ASP.NET Core.

### 2.2 `FeteadApi.Infrastructure/DependenciesInjection.cs`

Même forme que les autres modules (`//DailyApp`…), à la fin de `Init` :

```csharp
using FeteadApi.Domain.Interfaces.Monitoring;
using FeteadApi.Infrastructure.Repositories.Monitoring;
using FeteadApi.Infrastructure.Services.Monitoring;

        //Monitoring
        services.Configure<MonitoringOptions>(configuration.GetSection("Monitoring"));
        services.AddSingleton<MonitoringEnvironments>();
        services.AddScoped<IMonitoringRepository, MonitoringRepository>();
        services.AddScoped<IMonitoringSnapshotService, MonitoringSnapshotService>();
```

- `MonitoringEnvironments` est un **singleton**, comme `SqlConnectionFactory` : le statut d'écoute
  de chaque environnement doit être partagé entre le service d'écoute et l'API.
- Le repository ne passe pas par `SqlConnectionFactory`, qui ne connaît que `DefaultConnection` :
  le monitoring interroge une base par environnement.

### 2.3 `Program.cs`

```csharp
using FeteadApi.Presentation.Monitoring;

// Avec les autres services, après DependenciesInjection.Init(...) :
builder.Services.AddSignalR();
builder.Services.AddHostedService<MonitoringListenerService>();

// Après app.MapControllers(); :
app.MapHub<MonitoringHub>("/monitoringHub");
```

`MonitoringListenerService` est un service de fond (il vit aussi longtemps que l'application) :
il ne reçoit pas `IMonitoringRepository` directement, il crée un scope à chaque besoin.

⚠️ Tant que les lignes des § 2.2 et § 2.3 ne sont pas ajoutées, `MonitoringController` est quand
même découvert par `AddControllers()` : un appel à `/monitoring/*` échouerait (services non
enregistrés). Il n'y a aucun impact sur les autres endpoints.

### 2.4 `appsettings.json`

```json
"Monitoring": {
  "Environments": [
    { "Code": "LOCAL", "ConnectionStringVariable": "StyxConnectionLocal" },
    { "Code": "POC",   "ConnectionStringVariable": "StyxConnectionPoc" },
    { "Code": "INT",   "ConnectionStringVariable": "StyxConnectionInt" }
  ],
  "LatestJobsLimit": 50,
  "TaskListLimit": 1000,
  "MaxRangeDays": 92
}
```

Les chaînes restent dans les variables d'environnement `StyxConnection*`, lues directement
(`appsettings.json` est ré-ajouté après les variables dans `Program.cs` et passerait devant).
Une variable absente rend l'environnement `Unavailable` sans empêcher le démarrage.

### 2.5 Carte du portail (`frontend/index.html`) — livrée

Le portail est fourni dans `frontend/index.html`, avec la carte **08 — STYX Monitoring**
ajoutée et la carte « À venir » renumérotée **09** (elle portait aussi le numéro 07).

⚠️ Ce fichier est une **reconstitution** : version historique de `sgdesign` (`fet/index.html`)
complétée d'après les photos du portail actuel (bloc utilisateur, carte LocalDb, jQuery).
L'icône de la carte LocalDb était coupée sur la photo et a été complétée.
**Comparer avec le vrai fichier (diff) avant de le remplacer**, ou reporter uniquement la carte :

```html
<a class="app-card" href="/pages/monitoring/">
  <span class="app-num">08</span>
  <div class="app-icon"><svg viewBox="0 0 24 24"><polyline points="22 12 18 12 15 21 9 3 6 12 2 12"/></svg></div>
  <div class="app-name">STYX Monitoring</div>
  <div class="app-desc">Supervision temps réel des DataJobs et de leurs tâches, multi-environnements.</div>
  <div class="app-arrow">Ouvrir →</div>
</a>
```

### 2.6 Base de données — extension du trigger (décision D5)

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

### 2.7 Base de données — index (recommandés)

Utilisés par le comptage des tâches et la résolution tâche → job :

```sql
CREATE INDEX IF NOT EXISTS ix_data_task_job_id      ON public.data_task(job_id);
CREATE INDEX IF NOT EXISTS ix_data_task_external_id ON public.data_task(external_id);
```

---

## 3. Vérifications après intégration

1. Démarrage : log `[Monitoring:LOCAL] Écoute active` pour chaque environnement configuré.
2. `GET /monitoring/environments` → statuts `Listening`.
3. `GET /monitoring/snapshot?envs=LOCAL` → KPI, séries, jobs.
   `GET /monitoring/snapshot?envs=LOCAL&name=alm` → seul le tableau est filtré.
   `GET /monitoring/jobs/LOCAL/<id>/tasks` → tâches et commandes du job.
4. Page `/pages/monitoring/` : pastille « En direct », puis un `UPDATE public.data_job SET state = …`
   sur un job de test doit apparaître dans la timeline sans rechargement.
5. Onglet Réseau du navigateur : aucune requête périodique vers `/monitoring/*`.
6. Couper une base (ou son accès réseau) : l'environnement passe en `Reconnecting`, les autres continuent ;
   au retour, la page recharge automatiquement son snapshot.
