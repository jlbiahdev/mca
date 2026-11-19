# Mail aux architectes — Taskflow de nouveau en service Windows

> À décider avant envoi : le paragraphe « Portabilité vers Linux » concerne Taskflow. Les tâches UFX dépendent d'Excel, qui n'existe pas sous Linux. Pour éviter tout malentendu, on peut ajouter à la fin de ce paragraphe : « Les tâches UFX, qui dépendent d'Excel, devront être traitées à part. »

---

**Objet :** Taskflow : retour en service Windows

Bonjour à tous,

Taskflow tourne de nouveau en service Windows.

**Le problème**

Les tâches UFX pilotent Excel. Lancé depuis un service Windows, Excel a besoin que le profil système de la machine contienne un dossier Desktop, que Windows ne crée plus par défaut depuis Windows Server 2008.

**La solution**

Création de deux dossiers vides :

- `C:\Windows\System32\config\systemprofile\Desktop`
- `C:\Windows\SysWOW64\config\systemprofile\Desktop`

Résultat : Taskflow tourne en service Windows, sous le compte de service, et les tâches UFX s'exécutent normalement. Ces deux dossiers sont à créer sur chaque serveur qui exécute Taskflow.

**Portabilité vers Linux**

Taskflow est développé en .NET et s'exécute en service grâce à la méthode d'extension `UseWindowsService()`. Sous Linux, il pourra s'exécuter en service systemd avec son équivalent `UseSystemd()` (paquet `Microsoft.Extensions.Hosting.Systemd`). Les deux appels peuvent coexister dans le même code : chacun ne s'active que sur son système.

**Références**

- Contournement décrit par le support Microsoft : https://learn.microsoft.com/en-us/archive/blogs/sqlserverfaq/unable-to-open-excel-files-using-a-cscript-in-sql-server-jobs
- Origine du problème : https://data.bangtech.com/office/excel_2007_automation_in_windows_2008_service.htm
- Position officielle de Microsoft : l'automatisation d'Office depuis un service n'est ni recommandée ni prise en charge (KB 257757) : https://support.microsoft.com/en-us/help/257757

Cordialement,
Jean Luc
