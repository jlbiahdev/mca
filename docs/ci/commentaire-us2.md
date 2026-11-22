**US 2 — Changements par rapport au ticket initial**

**1. Un script plutôt qu'une suite de commandes**

Le ticket prévoyait de créer la tâche planifiée avec quatre commandes à enchaîner dans la même console (`New-ScheduledTaskAction`, `New-ScheduledTaskTrigger`, `New-ScheduledTaskSettingsSet`, `Register-ScheduledTask`). C'était fastidieux et source d'erreurs : une variable perdue en changeant de console, ou un nom de compte mal saisi dans `-User`, qui donnait `The parameter is incorrect` sans autre explication.

Elles sont remplacées par un script, `install-task.ps1`, livré à côté de `ci-dev.ps1` :

- il récupère lui-même le compte qui le lance : aucun nom de domaine ou de compte à saisir ;
- il crée la tâche, ou la remplace si elle existe déjà ; en cas d'échec, l'ancienne tâche est conservée ;
- il affiche le résultat (compte, mode d'exécution, script, fréquence) ;
- il se rejoue à l'identique, ce qui facilite la documentation et la réinstallation sur une autre machine.

**2. Mode d'exécution : session ouverte, sans mot de passe enregistré**

L'enregistrement de la tâche avec le mot de passe du compte (« Exécuter même si l'utilisateur n'est pas connecté ») est refusé sur le poste (erreur `0x80070520`) : une stratégie de l'entreprise interdit de stocker ce mot de passe.

Par défaut, le script crée donc une tâche qui tourne tant que la session du compte est ouverte, même verrouillée. Elle s'arrête à la déconnexion ou au redémarrage de la machine, et reprend à la connexion suivante.

L'option `-RunWhenLoggedOff` crée la tâche avec mot de passe, pour une machine où la stratégie l'autorise. Pour un fonctionnement session fermée sur ce poste, il faudra un compte de service autorisé.

**3. Privilèges les plus élevés**

Les dossiers `D:\ci` ont été créés depuis une console administrateur. Lancée sans élévation, la tâche n'avait pas le droit d'y écrire : elle échouait avant même d'ouvrir son journal (`Access to the path … is denied`). La tâche tourne donc avec les privilèges les plus élevés du compte.

**4. `conhost.exe --headless` plutôt que `powershell.exe`**

En mode session ouverte, la tâche s'exécute dans la session de l'utilisateur : la console de PowerShell s'affichait à chaque passage, donc chaque minute.

L'option `-WindowStyle Hidden` de PowerShell ne suffit pas : Windows ouvre la console avant que PowerShell ne lise l'option, d'où un clignotement chaque minute.

La tâche lance donc `conhost.exe --headless powershell.exe …` : PowerShell démarre sans console, et aucune fenêtre ne s'affiche. Le passage a été vérifié sur la machine de POC (une ligne par minute dans le journal, aucune fenêtre).

Point à vérifier à l'US 8 : on ne sait pas encore si `conhost` transmet le code de sortie du script à la tâche. Si ce n'est pas le cas, `LastTaskResult` reste à `0` même quand le script échoue ; le journal du jour reste alors la référence.

**5. Corrections du script `ci-dev.ps1` faites pendant l'US**

- Le chemin par défaut de la configuration était calculé dans le bloc `param()`, où `$PSScriptRoot` est vide en Windows PowerShell 5.1 : il est maintenant calculé dans le corps du script.
- Un accès refusé sur le fichier de verrou s'affichait comme « Un passage est déjà en cours » : seul un fichier réellement occupé donne maintenant ce message ; toute autre erreur affiche sa vraie cause.
- Une erreur d'écriture du journal masquait le message d'origine : elle est maintenant signalée par un avertissement, sans perdre le message.

**Résultat**

US 2 validée : la tâche `Styx.CiDev` se lance chaque minute (`LastTaskResult` = `0`), sans fenêtre, et chaque passage est écrit dans le journal du jour.
