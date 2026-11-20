# Tickets — CI/CD Styx : package et publication sans runner GitLab

Ce document est une mise à jour des tickets GitLab de l'Epic « CI/CD Styx : déploiement en dev depuis une MR ». Ils supposaient un pipeline GitLab (jobs, artefacts de job, `environment`, `resource_group`, `when: manual`). La création de runners GitLab est bloquée sur l'instance : ce travail est fait par nos propres runners de scrutation.

- **CI** : le runner `ci-dev` (voir `ci-runner-v0/tickets.md` dans sgdesign) compile chaque MR et publie le résultat dans GitLab.
- **CD** : le runner-cd, construit à partir des scripts `devops/`, package et publie la version après chaque merge dans `main`.

## Décisions

- **Périmètre** : on s'arrête au push du package sur Artifactory. Le déploiement est reporté.
- **Artifactory** : le package est publié sur Artifactory dès la dev (et non plus conservé en artefact de job). La clé Artifactory est stockée dans GitLab.
- **Sources** : les scripts reçoivent une racine de sources et se comportent de la même façon, qu'elle vienne de git (en dev) ou de l'API GitLab (en int).
- **Configuration** : tout ce qui varie d'un environnement à l'autre est dans `devops/config/<env>.psd1`, sans aucun secret ; les secrets sont lus dans des variables d'environnement.
- **Version** : numéro du pipeline externe créé par l'API Commit Status (à tester, voir US 4.2).

## État des tickets

| Ticket | Titre | Action |
|---|---|---|
| US 2.1 | Créer le runner de projet dans GitLab | Fermé (bloqué par l'instance, remplacé par le runner de scrutation) |
| US 4.1 | Package | **Réécrit** |
| US 4.2 | Format de version | **Réécrit** |
| US 5.1 | Décisions préalables au déploiement | Reporté |
| US 5.2 | Déployer l'API en dev | Reporté |
| US 5.3 | Déployer Taskflow en dev | Reporté |
| US 5.4 | Déployer HpcLite en dev | Reporté |
| US 6.1 | Créer les variables GitLab | **Corrigé** |
| US 6.2 | Injecter les secrets au déploiement | Reporté |
| US 6.3 | Restreindre les variables de pipeline manuelles | À fermer (sans objet) |
| US 7.1 | Documentation | **Ajusté** |

Les tickets reportés restent tels quels dans GitLab ; ils seront revus quand le déploiement reviendra dans le périmètre (ils devront alors devenir des étapes du runner-cd, et non des jobs).

---

## US 4.1 — Package et publication sur Artifactory

**Contexte** : on compile **une seule fois**, après le merge dans `main`, et c'est ce package-là qui sera déployé plus tard. Il n'y a pas de job GitLab : c'est le runner-cd qui détecte le nouveau commit sur `main`, package puis publie.

**À faire** :

1. Le runner-cd détecte un nouveau commit sur `main` et récupère ses sources par l'API GitLab.
2. Il lance le build puis le package à partir de ces sources, avec la version définie par US 4.2.
3. Il pousse le `.nupkg` sur Artifactory ; la clé est lue dans une variable GitLab (US 6.1) et n'apparaît jamais dans le journal.
4. Il publie le statut du commit dans GitLab : « en cours » au démarrage, puis « réussi » ou « échoué ».

**Fini quand** :

- après un merge dans `main`, le package est disponible sur Artifactory avec la version attendue ;
- le package contient `content/styx_publish.zip` avec la structure attendue par `deploy.ps1` (`api/`, `taskflow/`, `hpclite/…`) ;
- aucun package n'est produit pour une MR ;
- un échec du build, du package ou du push passe le statut du commit à « échoué », sans package partiel publié ;
- aucun secret ne figure dans le dépôt, dans le package ni dans les journaux.

**Dépendances** : US 4.2, US 6.1, runner `ci-dev` (tickets 1 à 4).

## US 4.2 — Version issue du pipeline externe GitLab

**Contexte** : sans runner GitLab, GitLab n'exécute pas de pipeline et n'attribue donc pas de numéro. Mais quand le runner publie un statut sur un commit par l'API Commit Status (`POST /projects/:id/statuses/:sha`), GitLab crée un pipeline **externe**, avec un identifiant. Ce numéro peut servir de version, et l'exécution apparaît dans l'onglet Pipelines et sur la page du commit.

**À faire** :

1. **Test** : sur une branche de test, publier un statut sur un commit par l'API et vérifier que :
   - la réponse contient l'identifiant du pipeline (`pipeline_id`) ;
   - le pipeline apparaît dans Build > Pipelines ;
   - deux statuts publiés sur le même commit (début, puis fin) restent dans le même pipeline.
2. **Si le test réussit** : le runner-cd publie le statut « en cours » avant le package, lit l'identifiant du pipeline dans la réponse et l'utilise pour la version.
3. **Si le test échoue** : on garde le format actuel `Styx.Publish.1.AAMMJJ.HHMMSS`.
4. Dans les deux cas, les scripts de package reçoivent la version en paramètre.

**Fini quand** :

- le résultat du test et le format retenu sont notés dans ce ticket ;
- deux exécutions successives du runner-cd produisent deux versions distinctes et croissantes ;
- depuis une version publiée sur Artifactory, on retrouve son commit (et son pipeline, si l'option est retenue).

**Dépendances** : aucune.

## US 6.1 — Créer les variables GitLab

**Contexte** : le runner lit les secrets par l'API GitLab avec son jeton. Deux réglages de GitLab ne s'appliquent pas à ce cas :

- une variable **Masked and hidden** n'est plus lisible par l'API, donc le runner ne pourrait pas la lire ;
- **Protect variable** ne restreint que les jobs CI : avec un jeton Maintainer, l'API la lit quand même.

**À faire** : pour chaque secret nécessaire au package et à la publication (clé Artifactory, au minimum), créer une variable de projet :

- Visibility : **Masked** (et non *Masked and hidden*) ;
- Environments : `dev` si la portée par environnement est retenue ; dans ce cas, le runner lit la variable avec le filtre d'environnement de l'API ;
- le nom de la variable est celui déclaré dans `devops/config/<env>.psd1` (par exemple `ARTIFACTORY_API_KEY`).

**Fini quand** :

- le runner lit la variable par l'API et pousse sur Artifactory ;
- la valeur n'apparaît dans aucun journal du runner ni de GitLab ;
- le jeton du runner est protégé : droits minimaux, date d'expiration, stockage chiffré sur la machine (DPAPI), jamais dans un fichier versionné.

**Dépendances** : aucune.

## US 6.3 — Restreindre les variables de pipeline manuelles

**À fermer** : ce ticket portait sur les variables imposées au lancement d'un pipeline GitLab. Sans pipeline GitLab, il est sans objet. La protection du jeton du runner, qui le remplace, est intégrée à US 6.1.

## US 7.1 — Documenter le runner et le pipeline

**Contexte** : la procédure doit pouvoir être suivie par quelqu'un qui ne connaît pas le code, en particulier pour réinstaller les runners sur une autre machine plus tard.

**À faire** :

1. Procédure d'installation des runners de scrutation (`ci-dev` et runner-cd) : tâche planifiée, jeton, configuration `<env>.psd1`. Étapes numérotées, une commande par bloc, résultat attendu après chaque commande.
2. Description du pipeline : ce qui se passe sur une MR (build, statut dans la MR), ce qui se passe sur `main` (package, version, publication sur Artifactory), comment lire un échec (statut du commit, journal sur la machine).

**Fini quand** : la procédure a été rejouée de bout en bout sur la machine de POC, dans un nouveau terminal.

**Dépendances** : US 4.1, US 4.2, US 6.1.
