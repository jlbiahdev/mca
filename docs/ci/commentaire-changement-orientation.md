**Changement d'orientation : runner de scrutation à la place d'un runner GitLab**

**Constat**

La création d'un runner GitLab est bloquée par la stratégie de l'instance (US 2.1). Sans runner, aucun pipeline GitLab ne peut s'exécuter : les tickets qui reposaient sur des jobs (`.gitlab-ci.yml`, artefacts de job, environnements, boutons manuels) ne sont pas réalisables tels quels.

**Décision**

Le travail du runner est fait par un script PowerShell installé sur la machine de POC (`ci-dev.ps1`), lancé chaque minute par une tâche planifiée. À chaque passage, il interroge GitLab par son API :

- il détecte les merge requests ouvertes et leur dernier commit ;
- il télécharge les sources du commit (zip généré par GitLab, sans git sur la machine) ;
- il compile avec le même script de build qu'en local ;
- il publie le résultat dans la MR par l'API Commit Status (statut `ci-dev` « en cours », puis « réussi » ou « échoué »).

**Ce qui ne change pas**

- Chaque MR est compilée avant le merge, et le résultat s'affiche dans la MR.
- Aucun secret dans le dépôt, les scripts ou les journaux : le jeton GitLab est chiffré sur la machine (DPAPI), lisible uniquement par le compte de la tâche.

**Ce qui change dans les tickets**

- **F2 et F3** (runner GitLab, pipeline de MR) : remplacés par l'Epic « Runner CI de dev », découpé en 9 US testables une à une (connexion à GitLab, lancement automatique, détection des MR, mémoire des commits traités, récupération des sources, build, statut dans GitLab, build en échec, documentation).
- **F4** (package) : le package sera produit et publié sur Artifactory par un second runner de scrutation, après chaque merge dans `main`. Le numéro de version pourra venir du pipeline « externe » que GitLab crée quand on publie un statut de commit (à vérifier pendant l'US 7 du runner CI).
- **F5** (déploiement) : reporté. Le périmètre s'arrête pour l'instant au push sur Artifactory.
- **F6** : les variables GitLab lues par le runner doivent être « Masked » (une variable « Masked and hidden » n'est pas lisible par l'API). La restriction des variables de pipeline manuelles (US 6.3) est sans objet.

**Avancement**

Sur la machine de POC :

- **US 1** validée : connexion à GitLab avec le jeton chiffré.
- **US 2** validée : le runner passe chaque minute, sans fenêtre, et chaque passage est journalisé.

Contrainte rencontrée : la stratégie du poste interdit d'enregistrer un mot de passe pour une tâche planifiée. Le runner tourne donc tant que la session du compte est ouverte (même verrouillée), et s'arrête à la déconnexion ou au redémarrage jusqu'à la connexion suivante. Pour un fonctionnement session fermée, il faudra un compte de service autorisé.

**Prochaines étapes**

US 3 à 8 du runner CI (détection des MR jusqu'au statut dans la MR), puis la documentation d'installation et d'exploitation (US 9).
