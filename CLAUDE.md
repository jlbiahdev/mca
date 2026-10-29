# CLAUDE.md — mca

Dépôt de livraison du **Module 08 « STYX Monitoring »** de FETEAD, en attente d'intégration
dans la solution FETEAD (non présente ici).

Le dossier `tools/` contient de petits outils de poste de développement, indépendants de FETEAD
(`tools/localdb-autostart` : démarrage automatique de PostgreSQL local ;
`tools/fetead-autostart` : démarrage automatique de FETEAD).

## Règles de travail

- Ne jamais committer ni pousser sur `main` ; une branche de travail et une PR par livraison.
  Le merge est décidé par Jean uniquement.
- Identité de commit : `Claude <noreply@anthropic.com>`.
- Ce dépôt ne contient que du **nouveau code**, à une exception validée : le portail
  `frontend/index.html` (carte 08). Toute autre modification de code existant de FETEAD
  (`Program.cs`, schéma SQL) est décrite dans `docs/module08-styx-monitoring/INTEGRATION.md`
  et n'est pas appliquée ici sans accord explicite.
- Mettre à jour `summary.md` (contrat GRIM) à chaque ouverture de PR.
- Confidentialité : aucune chaîne de connexion, aucun secret dans le code, les logs ou les documents.

## Conventions du projet

- **Le code doit être compréhensible par un développeur junior** : simplicité avant SOLID,
  pas d'abstraction inventée ; suivre les conventions déjà en place dans FETEAD, que l'équipe connaît :
  interface dans `Domain.Interfaces.<Module>`, implémentation dans `Infrastructure.Repositories.<Module>`
  ou `Infrastructure.Services.<Module>`, enregistrement `AddScoped<IX, X>()` dans
  `DependenciesInjection.Init`, écoute PostgreSQL comme `TaskFlowNotificationListener`.
  Commentaires en français sur le « pourquoi ».
- Stack : .NET 8, ASP.NET Core, Dapper, Npgsql, SignalR ; front HTML/CSS/JS + jQuery, sans framework.
- Design System FETEAD strict : réutiliser les composants de `styles.css` et de TaskFlow
  (`stat-card`, `card`, `table-wrap`, `timeline`, `conn-dot`, `mode-tab`).
- Aucun rafraîchissement périodique des données côté navigateur : snapshot REST + événements SignalR.
- Décisions validées et points ouverts : `docs/module08-styx-monitoring/CADRAGE.md`.

## Tests

- Front : `node --test tests/frontend/monitoring.test.js` (aucune dépendance).
- Back : `dotnet test tests/FeteadApi.Monitoring.Tests` (projet autonome qui compile les sources
  d'Infrastructure ; à passer en `ProjectReference` une fois intégré à la solution FETEAD).
