**US 8 — Build en échec et erreurs du runner**

**Validation (MR !6)**

| Cas | Résultat |
|---|---|
| Commit qui ne compile pas | statut « échoué », commentaire avec les 2 erreurs (CS1514, CS1513) et le lien vers le journal, journal téléchargeable depuis la MR |
| Merge | bloqué par « Pipeline must succeed » tant que le statut est « échoué » |
| Commit de correction | statut « réussi », job qui ouvre le journal, pas de commentaire ; MR « Ready to merge » |
| Erreur du runner (script de build introuvable) | statut « échoué » avec le message, aucun commentaire, commit absent de `processed.txt` |
| Erreur corrigée, relance | même commit repris dans le même pipeline (913231), build réussi, statut repassé à « réussi » |

**Remarques**

- L'auteur du commit affiché est le nom configuré dans git sur le poste du développeur (`user.name`), pas son nom GitLab.
- Les accents mal encodés dans le journal du build viennent du script de build (point déjà noté), pas du dépôt du journal.
- La fusion reste manuelle. GitLab propose « Set to auto-merge » sur chaque MR ; ce n'est pas utilisé pour l'instant.
