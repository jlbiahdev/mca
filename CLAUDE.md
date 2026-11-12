# CLAUDE.md — mca

Dépôt de livraison de modules FETEAD, vidé après chaque intégration dans la solution FETEAD
(non présente ici). Une copie privée et pérenne est tenue dans `sgdesign`.

Chantier en cours : **`got`**, versioning de la solution FETEAD entre le poste de chaque développeur et
un share réseau commun. Spécification : `docs/got/SPEC.md` (copie dans `sgdesign/got/SPEC.md`).

## Règles de travail

- Ne jamais committer ni pousser sur `main` ; une branche de travail et une PR par livraison.
  Le merge est décidé par Jean uniquement.
- Identité de commit : `Claude <noreply@anthropic.com>`.
- Toute modification de la spécification est reportée dans la copie de `sgdesign`, par PR.
- Ce dépôt ne contient que du **nouveau code**. Toute modification de code existant de FETEAD est décrite
  dans un guide d'intégration et n'est pas appliquée ici sans accord explicite.
- Mettre à jour `summary.md` (contrat GRIM) à chaque ouverture de PR.
- Confidentialité : aucun chemin de share réel, aucune chaîne de connexion, aucun secret dans le code,
  les logs ou les documents.

## Conventions du projet

- Code compréhensible par un développeur junior : simplicité avant SOLID, pas d'abstraction inventée ;
  suivre les conventions FETEAD : interface dans `Domain.Interfaces.<Module>`, implémentation dans
  `Infrastructure.Services.<Module>`, enregistrement dans `DependenciesInjection.Init`.
  Commentaires en français sur le « pourquoi ».
- Stack : .NET 8, ASP.NET Core ; front HTML/CSS/JS + jQuery, sans framework.
- Design System FETEAD strict.
- Décisions validées et points ouverts de `got` : `docs/got/SPEC.md` (§3 et §10).
