# Mail à l'équipe Artifactory : restauration NuGet par un compte technique

Remplacer `<kear_uid>` par l'identifiant KEAR de l'application avant l'envoi.

---

Bonjour <destinataire>,

Nous voulons que notre CI restaure les paquets NuGet de la solution directement depuis Artifactory, avec un compte technique (pas de compte personnel sur la machine de build). Nos essais bloquent sur deux points :

1. Droits de lecture

Avec le compte usr-cd de notre application, la restauration depuis notre dépôt virtuel nuget-virtual-<kear_uid> s'authentifie bien, mais le téléchargement de paquets publics renvoie 403 (par exemple Dapper 2.1.66, Newtonsoft.Json 13.0.4, NLog.Web.AspNetCore 6.1.3). Est-il possible de donner au compte usr-cd (ou à un autre compte technique de l'application) le droit de télécharger depuis ce dépôt virtuel, y compris les paquets publics qui ne sont pas encore en cache ?

2. Adresse v3

Avec l'adresse NuGet v2 du dépôt virtuel, seules 100 versions par paquet sont renvoyées, ce qui masque les versions récentes (par exemple Microsoft.Extensions.Hosting 10.0.3 introuvable, version la plus proche 8.0.0-preview). Quelle adresse v3 (index.json) faut-il utiliser pour ce dépôt virtuel ?

Pour information, le push de nos packages (compte usr) et le téléchargement de nos propres packages (compte usr-cd) fonctionnent.

Merci d'avance,
<signature>
