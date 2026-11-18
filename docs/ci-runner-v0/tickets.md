# Tickets — Runner CI de dev

Objectif : la **CI**. À chaque merge request, le runner récupère les sources, lance le build et publie le résultat dans GitLab.

La création de runners GitLab étant bloquée sur l'instance, le runner est un script (`ci-dev.ps1`) lancé toutes les minutes par une tâche planifiée sur la machine de POC.

Le déploiement (CD, avec `deploy.ps1`) viendra après ces quatre tickets.

---

## Ticket 1 — Création du runner v0

**Contexte** : le runner doit détecter les merge requests et récupérer leurs sources. Ni build ni statut GitLab à ce stade.

**À faire** :

1. Écrire `ci-dev.ps1` v0 : toutes les minutes, liste des MR ouvertes ; pour chaque MR dont le dernier commit n'est pas encore traité, sources de ce commit déposées dans `D:\ci\build`, puis commit noté dans le fichier « j'ai travaillé » (`D:\ci\runner\processed.txt`).
2. Prérequis sur la machine de POC : git 2.31 ou plus ; jeton d'accès au projet (rôle Developer, scopes `read_api` et `read_repository`) ; fichier chiffré du jeton.
3. Installer le script dans `D:\ci\runner` et créer la tâche planifiée `Styx.CiDev` (toutes les minutes).

**Fini quand** :

- un lancement à la main sans MR ouverte affiche `Aucune MR à traiter` ;
- la tâche planifiée est à l'état `Ready` et se lance chaque minute (`Get-ScheduledTaskInfo Styx.CiDev`) ;
- aucun jeton n'apparaît dans le script, les journaux ou l'historique de la console.

**Dépendances** : aucune.

---

## Ticket 2 — Détection d'une MR

**Contexte** : vérifier de bout en bout que le runner voit une MR et récupère ses sources.

**À faire** :

1. Créer une branche de test et y pousser un commit.
2. Créer une MR depuis cette branche.
3. Attendre le passage du runner (une minute au plus).
4. Pousser un deuxième commit sur la même branche.

**Fini quand** :

- dans la minute qui suit la création de la MR, `D:\ci\build` contient les sources de son dernier commit, et `processed.txt` contient ce commit ;
- le journal du runner indique la MR et le commit traités ;
- au lancement suivant, sans nouveau commit, le runner ne fait rien ;
- après le deuxième push, le runner traite le nouveau commit dans la minute.

**Dépendances** : ticket 1.

---

## Ticket 3 — Build réussi, statut positif dans GitLab

**Contexte** : brancher le build dans le runner et publier le résultat dans la MR.

**À faire** :

1. Après la récupération des sources, le runner lance le script de build sur `D:\ci\build`.
2. Le build écrit son **rapport** dans un fichier (résultat, commit, date, chemin du journal de build). Format et emplacement à fixer dans ce ticket.
3. Le runner lit ce rapport et publie le **statut du commit** dans GitLab (`ci-dev`) : « en cours » au démarrage du build, puis « réussi ».
4. Remplacer le jeton par un jeton avec le scope `api` (publication des statuts) et `read_repository`.
5. Test : pousser un code **sans erreur** sur la branche de la MR.

**Fini quand** :

- la MR affiche le statut `ci-dev` « en cours » pendant le build, puis « réussi » ;
- le fichier de rapport existe et indique le succès ;
- le commit est noté dans `processed.txt` seulement après la publication du statut.

**Dépendances** : ticket 2.

---

## Ticket 4 — Build en échec, statut négatif dans GitLab

**Contexte** : vérifier qu'un build en erreur est annoncé comme tel dans la MR.

**À faire** :

1. Pousser sur la branche de la MR un code **qui ne compile pas**.
2. Vérifier que le runner publie « échoué », avec une description courte et le chemin du journal de build sur la machine.
3. Vérifier dans Settings > Merge requests > Merge checks que « Pipelines must succeed » bloque le merge sur ce statut (à confirmer : comportement des statuts externes).
4. Corriger le code, pousser, et vérifier que le statut repasse à « réussi ».

**Fini quand** :

- la MR affiche le statut `ci-dev` « échoué » ;
- le rapport de build indique l'échec et le journal de build contient l'erreur de compilation ;
- après correction, le statut repasse à « réussi » ;
- on sait si GitLab bloque le merge sur ce statut.

**Dépendances** : ticket 3.
