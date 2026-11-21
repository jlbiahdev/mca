**US 3 — `.gitlab-ci.yml` par défaut et « Pipelines must succeed »**

À la création du dépôt, GitLab a ajouté un `.gitlab-ci.yml` qui inclut le modèle de détection de secrets (`Security/Secret-Detection.gitlab-ci.yml`).

Conséquence : chaque commit crée un pipeline GitLab. Faute de runner, ce pipeline reste indéfiniment en attente (« pending »). Or l'option **Pipelines must succeed** est activée sur le projet : tant que ce pipeline n'aboutit pas, aucune MR ne peut être fusionnée.

**Décision** : supprimer ce `.gitlab-ci.yml` par une MR dédiée. Il ne s'exécute jamais, donc il ne protège rien aujourd'hui.

**À faire avant l'US 7** : une fois le fichier supprimé, le seul pipeline d'un commit sera le pipeline « externe » créé par le statut `ci-dev` du runner. « Pipelines must succeed » dépendra alors uniquement du résultat du build.

**Point à vérifier** : le comportement de « Pipelines must succeed » pour la MR de suppression elle-même, qui n'aura aucun pipeline. Si le merge reste bloqué, décocher l'option le temps du merge.
