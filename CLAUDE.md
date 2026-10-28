# CLAUDE.md — mca

Dépôt de livraison du **Module 08 « STYX Monitoring »** de FETEAD, en attente d'intégration
dans la solution FETEAD (non présente ici).

## Règles de travail

- Ne jamais committer ni pousser sur `main` ; une branche de travail et une PR par livraison.
  Le merge est décidé par Jean uniquement.
- Identité de commit : `Claude <noreply@anthropic.com>`.
- Ce dépôt ne contient que du **nouveau code**. Toute modification de code existant de FETEAD
  (`Program.cs`, portail, schéma SQL) est décrite dans `docs/module08-styx-monitoring/INTEGRATION.md`
  et n'est pas appliquée ici sans accord explicite.
- Mettre à jour `summary.md` (contrat GRIM) à chaque ouverture de PR.
- Confidentialité : aucune chaîne de connexion, aucun secret dans le code, les logs ou les documents.

## Conventions du projet

- Stack : .NET 8, ASP.NET Core, Dapper, Npgsql, SignalR ; front HTML/CSS/JS + jQuery, sans framework.
- Design System FETEAD strict : réutiliser les composants de `styles.css` et de TaskFlow
  (`stat-card`, `card`, `table-wrap`, `timeline`, `conn-dot`, `mode-tab`).
- Aucun rafraîchissement périodique des données côté navigateur : snapshot REST + événements SignalR.
- Décisions validées et points ouverts : `docs/module08-styx-monitoring/CADRAGE.md`.

## Tests

- Front : `node --test tests/frontend/monitoring.test.js` (aucune dépendance).
- Back : `dotnet test tests/FeteadApi.Monitoring.Tests` (projet autonome qui compile les sources
  d'Infrastructure ; à passer en `ProjectReference` une fois intégré à la solution FETEAD).
