# dev.psd1 — configuration de l'environnement de dev (sans secret)

Contenu du fichier, à copier dans `src\scripts\devops\config\dev.psd1`.

À enregistrer en UTF-8 avec BOM (Windows PowerShell 5.1).

```powershell
# Configuration de l'environnement de développement (poste local).
#
# Ce fichier ne contient aucun secret : la section Applications.SecretVariables
# liste uniquement le NOM des variables d'environnement qui doivent porter les
# secrets (définies sur le poste, ou injectées par le runner depuis GitLab).
#
# Les chemins peuvent utiliser des variables d'environnement Windows (%TEMP%).
# Les valeurs de Applications.Environment peuvent utiliser les marqueurs
# {HostName}, {BindHost}, {ApiPort}, {AgentPort} et {TaskFlowPort}.
@{
    Environment = 'dev'

    Network = @{
        # Nom d'hôte utilisé dans les URL transmises aux applications.
        HostName     = 'localhost'

        # Interface d'écoute des composants.
        BindHost     = '127.0.0.1'

        ApiPort      = 5002
        AgentPort    = 5200
        TaskFlowPort = 5100
    }

    DotNet = @{
        # Valeurs utilisées par la publication et le packaging.
        Configuration = 'Release'
        Runtime       = 'win-x64'
        SelfContained = $true
    }

    Paths = @{
        WorkRoot      = '%TEMP%\styx_build'
        PackageOutput = '%TEMP%\styx_packages'
    }

    Share = @{
        Path       = '\\XXXXX\XXXXX\publish\styx'
        ExtraFiles = @(
            'start_all.ps1'
            'env.ps1'
        )
    }

    Artifactory = @{
        BaseUrl        = 'https://XXXXX'
        Repository     = 'XXXXX'
        ApiKeyVariable = 'ARTIFACTORY_API_KEY'
    }

    Development = @{
        # Environnement .NET appliqué par run_all.ps1 -Development : .NET y
        # valide l'injection de dépendances (portées, construction des
        # services) et charge les user-secrets des projets.
        Environment = 'Development'
    }

    Applications = @{
        # Variables transmises à chaque composant démarré.
        Environment = @{
            DOTNET_ENVIRONMENT     = 'Test'
            ASPNETCORE_ENVIRONMENT = 'Test'
            TaskFlow__BaseUrl      = 'http://{HostName}:{TaskFlowPort}'
            TaskFlow__CallbackUrl  = 'http://{HostName}:{TaskFlowPort}/taskflow/taskState'
        }

        # Variables obligatoires portant un secret, vérifiées par run_all.ps1
        # (sauf avec -Development, où les user-secrets peuvent les fournir) :
        # leur valeur n'est jamais lue dans ce fichier ni affichée.
        SecretVariables = @(
            'ConnectionStrings__StyxConnection'
        )
    }
}
```
