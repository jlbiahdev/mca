**US 6 — Build d'intégration sur la machine du runner**

**Validation**

La tâche planifiée a construit le dernier commit de la MR !3 en Release, environnement `int` (« MR !3 : Build réussi »). Un commit en échec est traité une seule fois : seul un nouveau commit relance le build.

Les blocages rencontrés, dans l'ordre, et les parades retenues :

**1. Les scripts de dev dépendent de git**

La machine du runner n'a pas git : `dev/build.ps1` ne peut pas y tourner. Le build d'intégration a son propre script, `src/scripts/devops/int/build.ps1`, sans git. Sa configuration est lue dans `config/int.psd1`. Le build est toujours en Release : un build Debug n'a pas d'intérêt en intégration. `ci-dev.psd1` pointe sur ce script (`BuildScript`, `BuildEnvironment = 'int'`).

Point d'attention : après la mise à jour, recopier aussi `ci-dev.psd1`. Avec l'ancien fichier, le runner lançait encore `dev/build.ps1` en Debug.

**2. La source NuGet pointait sur un poste de développeur**

`src/backend/nuget.config` déclarait une source locale propre à un poste (`C:\Users\…\NuGetOfflinePackages`), absente ailleurs.

**3. Pas de source NuGet joignable depuis la machine**

nuget.org est interdit et bloqué, et l'Artifactory NuGet de l'entreprise est incomplet (ticket en cours). En attendant, le dépôt embarque ses paquets :
- `nugets\` : les paquets publics, à plat ;
- `nugets\socgen\<paquet>\<version>\` : les paquets internes.

Le `nuget.config` déclare ces deux dossiers, en chemins relatifs.

**4. Le `.gitignore` excluait les paquets**

La règle `*.nupkg` empêchait de versionner les paquets. Le dossier des paquets internes ne contenait que leur contenu extrait, sans les `.nupkg`, et la restauration échouait (`NU1101`), alors qu'elle passait sur le poste où ces fichiers existaient. Parade : `git add -f "nugets/*.nupkg"`, puis `git status --ignored --short nugets` pour vérifier que plus aucun paquet n'est ignoré.

**5. Les sources NuGet de la machine restaient consultées**

Les sources du `nuget.config` du dépôt s'ajoutent à celles de la machine, dont nuget.org par défaut. Pour un paquet absent du cache, NuGet interroge toutes les sources et échoue si l'une d'elles est injoignable (`NU1301`). Parade : `<clear />` dans `packageSources`, pour que seules les sources du dépôt soient utilisées.

**6. Des dépendances indirectes manquaient**

Les paquets internes dépendent de paquets publics qui n'étaient pas dans le dépôt (`NU1101`), et certaines versions exactes manquaient (`NU1603`). Parade : copier dans `nugets\` tous les paquets d'une restauration complète réussie sur un poste, en une seule fois, plutôt que paquet par paquet.

**7. Une DLL était lue sur un partage réseau**

`Styx.Domain` référençait `LibTabInput` par un chemin sur un partage réseau (`HintPath`). La machine du runner n'y accède pas : la tâche tourne sans mot de passe enregistré et n'a pas accès aux ressources réseau authentifiées. Parade : la DLL est copiée dans le dépôt (`src/backend/libs/CSTABINPUT/V1.14.3/`), et le `HintPath` est relatif. L'application n'est pas concernée : à l'exécution, elle charge sa copie de la DLL, présente dans le dossier de sortie.

**8. Incident pendant la mise en place**

Une commande `dotnet nuget push` lancée sur un dossier trop large a copié des dizaines de paquets indésirables dans `nugets\socgen`. Ils ont été supprimés avant le commit (`git clean` / suppression manuelle, puis `git restore` des fichiers versionnés). À retenir : toujours lister ce qu'une copie va faire avant de la lancer, et ne jamais utiliser `git add -f .`, qui ajoute tous les fichiers ignorés du dépôt.

**Reste ouvert**

- **Ticket Artifactory** : une fois l'Artifactory complet, il pourra remplacer les paquets embarqués.
- **Taille de l'archive** : 578 Mo à cause des paquets, soit environ 1 min 20 d'extraction par build.
- **Encodage** : les accents du journal de build sont mal encodés.
- **Avertissements `NU1900`** : l'audit de vulnérabilités interroge nuget.org, ce qui est sans effet sur le build.
