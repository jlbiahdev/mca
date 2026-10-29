---
ai_project: https://claude.ai/project/019cb16c-29f7-7073-97fc-d7b30e1290c1
status: en cours
---

## Attentes
- Relecture de la PR #3 : détail d'un job (tâches et commandes exécutées) et filtres ID / Nom du tableau DataJobs.
- Après validation dans `mca` : reporter ces évolutions dans `sgdesign`.

## Blocages
- Aucun

## Prochaines étapes
- Exécuter `dotnet test` (non exécuté par Claude : SDK .NET indisponible dans son environnement).
- Lever les points ouverts du cadrage (consommateurs de `scheduler_events`, espace des `external_id`, exposition réseau).
- Intégration dans FETEAD, étape par étape, selon `docs/module08-styx-monitoring/INTEGRATION.md`.
