# Mail aux architectes — Taskflow en tâche planifiée

Deux versions du mail expliquant le passage de Taskflow d'un service Windows à une tâche planifiée.

> À vérifier avant envoi : « l'ouverture de session automatique ne semble pas autorisée sur ces machines » est une déduction (option absente dans `netplwiz`, réglage imposé par une stratégie), non confirmée par l'équipe système.

---

## Version 1 — Expliquer et rassurer

**Objet :** Taskflow : passage du service Windows à une tâche planifiée

Bonjour à tous,

Je vous informe d'un changement dans le mode d'exécution du runner Taskflow, que je sais contraire à votre orientation.

**Le constat**

Lorsque le runner tourne en service Windows, les tâches UFX échouent. Lancé dans la session du compte de service, il les exécute sans erreur. Ces tâches pilotent Excel, qui a besoin d'une session utilisateur interactive : un service Windows tourne hors de toute session, Excel n'y fonctionne pas correctement.

**Ce que j'ai mis en place**

- Suppression du service Windows Taskflow sur le serveur concerné.
- Lancement du runner par une tâche planifiée, à l'ouverture de session du compte de service, dans sa session.
- Relance automatique toutes les 5 minutes s'il s'arrête, avec une seule instance à la fois.
- La session du compte reste ouverte en permanence (fermer la fenêtre du bureau à distance ne la ferme pas).

**Les limites connues**

- Après un redémarrage du serveur, Taskflow ne repart qu'à la reconnexion du compte en bureau à distance. L'ouverture de session automatique ne semble pas autorisée sur ces machines.
- Le fonctionnement repose sur une session ouverte en permanence.

Je reste disponible pour en discuter.

Cordialement,
Jean Luc

---

## Version 2 — Demander un arbitrage

**Objet :** Taskflow : tâche planifiée à la place du service Windows — arbitrage sur la dépendance Excel

Bonjour à tous,

J'ai dû faire passer le runner Taskflow d'un service Windows à une tâche planifiée. Je sais que cela ne va pas dans le sens de votre orientation, et j'aimerais votre avis sur la suite.

**Pourquoi ce changement**

En service Windows, les tâches UFX échouent ; lancé dans la session du compte de service, le runner les exécute correctement. Ces tâches pilotent Excel, qui a besoin d'une session utilisateur interactive, ce qu'un service Windows ne fournit pas.

**La solution en place**

- Tâche planifiée à l'ouverture de session du compte de service.
- Relance automatique toutes les 5 minutes en cas d'arrêt, une seule instance.
- Session du compte maintenue ouverte. Limite : après un redémarrage, il faut une reconnexion en bureau à distance pour relancer Taskflow (l'ouverture de session automatique ne semble pas autorisée sur ces machines).

**Le point à arbitrer**

La contrainte ne vient pas de Taskflow mais de la dépendance des tâches UFX à Excel. Tant qu'elle existe, Taskflow ne peut pas tourner en service Windows. La lever supposerait de traiter les fichiers sans passer par Excel : c'est un chantier à part entière, à évaluer.

Souhaitez-vous que ce chantier soit étudié ? En attendant, la tâche planifiée vous convient-elle comme solution transitoire ?

Cordialement,
Jean Luc
