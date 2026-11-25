@{
    # ── GitLab ──────────────────────────────────────────────────────────────
    # Adresse de GitLab, sans / final.
    GitLabUrl  = 'https://XXXXX'

    # Identifiant numérique du projet (page d'accueil du projet, menu « ⋮ »).
    ProjectId  = 0

    # Jeton GitLab chiffré par protect-token.ps1 (DPAPI, niveau machine) ;
    # lecture limitée à SYSTEM, aux administrateurs et au compte du runner.
    TokenFile  = 'D:\ci\runner\gitlab-token.dat'

    # ── Runner ──────────────────────────────────────────────────────────────
    # Dossier du runner : script, configuration, journaux, commits traités.
    RunnerRoot = 'D:\ci\runner'

    # ── Sources ─────────────────────────────────────────────────────────────
    # Dossier où sont déposées les sources du commit. Il est VIDÉ à chaque
    # commit : ne rien y mettre d'autre.
    BuildRoot  = 'D:\ci\build'

    # ── Build (CI, merge requests) ──────────────────────────────────────────
    # Script de build, relatif à la racine des sources téléchargées.
    BuildScript         = 'src\scripts\devops\int\build.ps1'

    # Environnement transmis aux scripts du dépôt
    # (src\scripts\devops\config\<env>.psd1).
    BuildEnvironment    = 'int'

    # Au-delà, le script (build, package ou push) est arrêté et déclaré en échec.
    BuildTimeoutMinutes = 30

    # Nom du statut affiché sur les commits des MR.
    StatusName = 'ci-int'

    # ── Publication (CD, branche main) ──────────────────────────────────────
    # Branche dont chaque nouveau commit est packagé et publié.
    CdBranch     = 'main'

    # Nom du statut affiché sur les commits de main.
    CdStatusName = 'cd-int'

    # Scripts de package et de push, relatifs à la racine des sources.
    PackageScript = 'src\scripts\devops\int\package.ps1'
    PushScript    = 'src\scripts\devops\int\push-artifactory.ps1'

    # Dossier des packages générés (un sous-dossier par version).
    PackageRoot   = 'D:\ci\packages'

    # Version du package : {0} est remplacé par le numéro du pipeline externe
    # GitLab (unique et croissant).
    VersionFormat = '1.0.{0}'

    # Variable de projet GitLab qui contient la clé Artifactory. Même nom que
    # Artifactory.ApiKeyVariable dans config\int.psd1 : le runner transmet la
    # clé au script de push sous ce nom.
    ArtifactoryKeyVariable = 'ARTIFACTORY_API_KEY'

    # Variables d'environnement transmises au build et au package pour la
    # restauration des paquets NuGet depuis Artifactory. Leurs valeurs sont
    # tirées de ArtifactoryKeyVariable (<compte>:<jeton>) ; les noms doivent
    # être ceux utilisés dans src\backend\nuget.config.
    BuildUsernameVariable = 'ARTIFACTORY_BUILD_USERNAME'
    BuildTokenVariable    = 'ARTIFACTORY_BUILD_TOKEN'
}
