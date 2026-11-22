**US 7 — Statut dans GitLab**

**Validation**

Testé sur la MR !6 (branche `feature/47-test-failed-build`), avec deux commits successifs :

| Commit | Pipeline externe | Résultat |
|---|---|---|
| version avec un bug volontaire | 913214 | « en cours » pendant le build, puis **échoué** |
| correction | 913215 | **réussi** |

- La MR affiche le pipeline et son résultat (« Pipeline #913215 passed »).
- Avec « Pipelines must succeed », le pipeline n'est plus un motif de blocage : la seule condition restante est le statut Draft de la MR.
- Le cas de l'échec, prévu à l'US 8, a été vérifié en même temps pour la publication du statut.

**Résultat à reporter dans l'US 4.2 (Epic CI/CD)**

- GitLab renvoie bien l'identifiant du pipeline externe : il est écrit dans le journal (`Pipeline externe GitLab : 913214`), et c'est le même que dans **Build > Pipelines**.
- Le pipeline est visible dans la MR et dans la liste des pipelines du projet.
- Il y a un seul pipeline par commit : le statut « en cours » et le statut final y sont rattachés.

**Écarts avec le ticket**

- Le jeton s'appelle `ci-int-poc-api` : la cible du runner est l'intégration (`int`).
- Le test a été fait sur la MR !6 plutôt que sur `test/ci-dev`, et le projet comptait 4 MR ouvertes au lancement manuel.

**Prérequis réalisés avant l'US**

Comme prévu à l'US 3, le `.gitlab-ci.yml` par défaut a été supprimé (MR !4). Cette MR n'ayant pas de pipeline réussi, « Pipelines must succeed » a dû être décoché le temps du merge, puis recoché.

**Remarques**

- Dans la liste des pipelines, la durée affichée est `00:00:00` et la colonne « Created by » est vide : c'est normal pour un statut publié par l'API avec un jeton de projet. GitLab ne mesure pas la durée d'un job externe et l'attribue au compte technique du jeton.
- Les anciens pipelines marqués « Warning » viennent du `.gitlab-ci.yml` supprimé.
