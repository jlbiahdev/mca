# US 6 — Configuration int à ajouter au dépôt styx-poc

À ajouter par une MR dans `src/scripts/devops/config/`, en **UTF-8 avec BOM** et CRLF. `build.ps1` et `StyxBuild.psm1` ne changent pas : avec `-RepositoryRoot`, ils n'appellent pas git.

Les valeurs `XXXXX` sont masquées : les renseigner avec les vraies valeurs dans le dépôt styx-poc (jamais dans mca). Pour le build, seule la section `DotNet` est utilisée ; les autres sont exigées par `StyxBuild.psm1`.

## src/scripts/devops/config/int.psd1

```powershell
# Configuration de l'environnement d'intégration (machine de POC, runner CI).
#
# Ce fichier ne contient aucun secret : la section Applications.SecretVariables
# liste uniquement le NOM des variables d'environnement qui doivent porter les
# secrets.
#
# Pour le build (runner CI), seule la section DotNet est utilisée ; les autres
# sections sont exigées par StyxBuild et serviront au package et au
# déploiement. Les valeurs XXXXX sont à renseigner.
@{
    Environment = 'int'

    Network = @{
        # Nom d'hôte de la machine de POC, utilisé dans les URL transmises aux
        # applications.
        HostName     = 'XXXXX'

        BindHost     = '0.0.0.0'

        ApiPort      = 5002
        AgentPort    = 5200
        TaskFlowPort = 5100
    }

    DotNet = @{
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
        ExtraFiles = @()
    }

    Artifactory = @{
        BaseUrl        = 'https://XXXXX'
        Repository     = 'XXXXX'
        ApiKeyVariable = 'ARTIFACTORY_API_KEY'
    }

    Development = @{
        Environment = 'Development'
    }

    Applications = @{
        Environment = @{
            DOTNET_ENVIRONMENT     = 'Test'
            ASPNETCORE_ENVIRONMENT = 'Test'
            TaskFlow__BaseUrl      = 'http://{HostName}:{TaskFlowPort}'
            TaskFlow__CallbackUrl  = 'http://{HostName}:{TaskFlowPort}/taskflow/taskState'
        }

        SecretVariables = @(
            'ConnectionStrings__StyxConnection'
        )
    }
}
```
