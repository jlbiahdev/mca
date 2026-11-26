# Créer la machine de build

Ce document explique comment obtenir la machine sur laquelle tournera le runner CI (la « machine de build »), avec son compte de service. Il se fait **une seule fois** par environnement, **avant** le [HOWTO d'installation du runner](howto/HOWTO.md).

La machine est une **VCS** (serveur virtuel) créée depuis les portails internes. Les valeurs propres à l'équipe (code application, noms des groupes, adresse mail) ne sont pas écrites ici : elles sont dans la fiche interne de l'équipe.

Dans ce document :

| Repère | Signification | Exemple |
|---|---|---|
| `<TRI>` | le trigramme de la machine | `STU` pour la machine de build |
| `<CODE>` | le code de l'application | voir la fiche de l'équipe |
| `<ENV>` | l'environnement | `DEV` |

---

## 1. Le trigramme

La création du trigramme dans **KAT** (`go/kat`) crée automatiquement ses entrées dans **DIN** (DoItNow, `go/din`), pour les trois environnements : DEV, UAT et PRD. Si le trigramme existe déjà dans DIN, passer à l'étape 2.

---

## 2. Créer les rôles

Les rôles donnent les droits sur la machine. Ils se créent dans DIN (`go/din`).

1. Aller dans **Access Governance > Roles > Create**.
2. Créer les trois rôles ci-dessous, avec leurs droits. Tous portent le trigramme de la machine (pour la machine de build : `H-STU_…`).

| Rôle | Droits |
|---|---|
| `H-<TRI>_<CODE>_<ENV>-WSA` | `wsa:rp:act_as_part_os`, `wsa:rp:adjust_mem_quotas`, `wsa:rp:administrators`, `wsa:rp:allow_authenticate`, `wsa:rp:backup_operators`, `wsa:rp:bypass_checking`, `wsa:rp:create_global_obj`, `wsa:rp:debug_prog`, `wsa:rp:deny_remote_desktop`, `wsa:rp:enable_deleg`, `wsa:rp:impersonate_auth`, `wsa:rp:lock_pages_mem`, `wsa:rp:logon_batch`, `wsa:rp:logon_locally`, `wsa:rp:logon_service`, `wsa:rp:perf_volume_tasks`, `wsa:rp:power_users`, `wsa:rp:profile_sys_perf`, `wsa:rp:remote_desktop_users`, `wsa:rp:replace_token`, `wsa:rp:users` |
| `H-<TRI>_<CODE>_<ENV>-ADMIN_VCS` | `accaas:readwrite_account`, `acl:read_all_client`, `acl:read_client`, `acl:read_serviceaccount`, `acl:read_subjects`, `acl:readwrite_client`, `acl:readwrite_serviceaccount`, `dns:admin_dns_records`, `dns:admin_dns_zones`, `dns:read_dns_records`, `dns:read_dns_zones`, `dns:read_resources`, `files:admin`, `files:read`, `files:rp:admin_smb`, `files:write`, `secret:delete`, `secret:read`, `secret:write`, `vcs:read`, `vcs:write`, `vcs:write_image` |
| `H-<TRI>_<CODE>_<ENV>-BASTION_VCS` | `virtualserver:rp:localadmin_windows` (administrer une machine VCS) |

---

## 3. Habiliter les personnes de l'équipe

Les personnes qui administrent la machine reçoivent les rôles `ADMIN_VCS` et `BASTION_VCS`.

1. Dans **Access Governance > Roles**, cocher le rôle `H-<TRI>_<CODE>_<ENV>-ADMIN_VCS`.
2. Cliquer sur **Actions > Grant Identities**.
3. Dans **Users**, ajouter l'adresse mail de chaque personne.
4. Recommencer avec le rôle `H-<TRI>_<CODE>_<ENV>-BASTION_VCS`.

---

## 4. Créer la machine (VCS)

1. Revenir à l'accueil de DIN (`go/din`).
2. Sélectionner **VCS**.
3. Cliquer sur **Create server > VCS Gen2**.
4. **Name** : le nom de la machine, selon la convention de l'équipe (pour la machine de build : `cld<env>bld<numéro>`, « cloud, environnement, build, numéro »).
5. **Next > Network > AD Item** : le domaine de l'environnement (voir la fiche de l'équipe).
6. **Next > Additional Information > Notify Mail** : la liste de diffusion de l'équipe, qui recevra les messages sur la machine.
7. Valider.

---

## 5. Créer le compte de service

C'est le compte avec lequel le runner tournera sur la machine.

1. Aller dans **Access Control > Create**.
2. **Name** : `ADMIN`. Le portail en fait `<TRI><CODE>D_ADMIN` (par exemple, pour la machine de build : `STU<CODE>D_ADMIN`).
3. **Description** : `Compte Admin Dev`.
4. Valider.

---

## 6. Récupérer le mot de passe du compte de service

1. Aller dans **Secrets > Full**.
2. Cliquer sur **Reauthenticate**, puis copier le secret dans le presse-papiers.
3. Dans le fil d'Ariane, ouvrir : identifiant de l'application > **shared** > identifiant > **WINDOWS** > `<TRI><CODE>D_ADMIN`.

Ne recopier ce mot de passe dans aucun fichier ni aucune note : il se relit ici quand on en a besoin.

---

## 7. Donner au compte l'accès aux partages

Ajouter le compte `<TRI><CODE>D_ADMIN` aux trois groupes des partages réseau HPC et Work (noms exacts dans la fiche de l'équipe) :

| Groupe | Accès |
|---|---|
| groupe HPC « Root » | partage HPC |
| groupe Work « R » | partage Work, lecture |
| groupe Work « C » | partage Work, modification |

---

## Ensuite

La machine et son compte existent : suivre le [HOWTO d'installation du runner](howto/HOWTO.md), à partir de l'étape 1.1.
