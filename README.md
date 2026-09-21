# Claude Code ↔ Visual Studio (pont MCP)

Extension Visual Studio qui héberge un serveur MCP dans `devenv.exe`, pour que Claude Code
puisse compiler, lire les erreurs, poser des points d'arrêt, piloter le débogueur et inspecter
les variables — sans quitter l'IDE.

Cible : **Visual Studio 2022 (17.x) et 2026 (18.x)**, projets **C# / .NET**.

## Installation

```powershell
pwsh tools\Build.ps1              # compile, produit le .vsix
pwsh tools\Build.ps1 -Install     # installe (Visual Studio doit être fermé)
```

Relancer Visual Studio, ouvrir une solution, puis vérifier dans la fenêtre **Sortie → Claude MCP** :
le port d'écoute, le rôle de hub et la commande d'enregistrement y sont écrits au démarrage.

```powershell
pwsh tools\Smoke-Test.ps1         # teste le serveur en HTTP brut, sans Claude Code
pwsh tools\Register-McpServer.ps1 # enregistre le serveur auprès de Claude Code
```

En développement, `F5` déploie dans la ruche expérimentale (`/rootsuffix Exp`) sans toucher à
l'installation principale.

## Architecture

```
Claude Code ──► http://127.0.0.1:5230/mcp   (URL statique, configurée une seule fois)
                        │
                 instance « hub »
                        ├─ traite localement si la cible est elle-même
                        └─ relaie en HTTP vers 127.0.0.1:<port d'instance>
```

Chaque instance de Visual Studio écoute **toujours** sur un port d'instance
(plage `51234-51299`) et tente **en plus** de se lier au port hub `5230`. Le bind fait office
d'élection : le système arbitre, il n'y a pas de course. Si l'instance hub se ferme, une autre
reprend le rôle en moins de 10 s — **sans reconfiguration côté Claude Code**.

Le registre `%TEMP%\claude-vs-mcp\instance-<pid>.json` publie pid, port et solution chargée.
Les entrées orphelines sont purgées sur trois critères cumulés (processus vivant, nom `devenv`,
date de démarrage concordante) pour résister au recyclage de PID par Windows.

## Choisir l'instance à piloter

- Une seule instance ouverte → rien à faire.
- Plusieurs → `list_instances`, puis `use_instance` (la sélection reste valable pour la session),
  ou bien le paramètre `instance` sur chaque appel.
- En cas d'ambiguïté, les outils **refusent de deviner** et renvoient la liste des choix : un build
  lancé dans la mauvaise solution coûte plus cher qu'une question.

## Outils exposés

| Groupe | Outils |
|---|---|
| Instances | `list_instances`, `use_instance`, `server_info` |
| Solution | `solution_info`, `list_projects`, `open_file`, `set_startup_project`, `set_configuration` |
| Build | `build`, `build_status`, `cancel_build`, `get_errors` |
| Débogueur | `debug_start`, `debug_stop`, `debug_pause`, `debug_continue`, `debug_step`, `debug_state` |
| Points d'arrêt | `set_breakpoint`, `list_breakpoints`, `clear_breakpoints` |
| Inspection | `evaluate`, `get_locals`, `get_stack` |
| Sorties | `list_output_panes`, `get_output_pane` |
| Sélection | `get_selection`, `take_pending_context` |

Les opérations longues (build, exécution) prennent un `wait_ms`. Au-delà, la réponse a le statut
`running` : **ce n'est pas une erreur**, c'est un état à ré-interroger avec `build_status` ou
`debug_state`. Le plafond serveur est de 120 s pour rester sous le timeout du client MCP.

## Envoyer une sélection depuis Visual Studio

Dans l'éditeur (`Ctrl+Alt+Maj+C` ou clic droit) et dans le menu contextuel de la fenêtre Sortie :
**« Envoyer à Claude Code »**. La sélection est figée au moment du clic — c'est le point important,
car entre le moment où tu sélectionnes et celui où tu formules ta demande, la sélection a souvent
déjà changé.

Deux chemins selon l'état de la connexion :

- **Claude Code connecté à l'IDE** (`/ide` dans le terminal) → la sélection est poussée
  directement dans le prompt sous forme de @-mention.
- **Sinon** → elle est mise en file, et Claude la récupère avec `take_pending_context`.

Sans rien envoyer du tout, `get_selection` lit la sélection courante à la demande.

### Comment fonctionne le push

Claude Code expose un protocole d'intégration IDE : l'extension ouvre un serveur WebSocket,
dépose `~/.claude/ide/<port>.lock` décrivant le port, l'`authToken` et les `workspaceFolders`,
et Claude Code s'y connecte. Par-dessus, c'est le même JSON-RPC que le pont HTTP — le
`McpDispatcher` est réutilisé tel quel — plus deux notifications que l'IDE peut pousser :
`at_mentioned` et `selection_changed`.

**Ce protocole est interne à Claude Code et non documenté publiquement.** Il a été reproduit
depuis l'extension Visual Studio Code officielle et peut changer sans préavis. S'il casse, le
pont HTTP continue de fonctionner et seul le push est perdu.

Deux conséquences concrètes de sa conception :

- `at_mentioned` ne transporte **pas de texte**, seulement `{filePath, lineStart, lineEnd}` :
  Claude relit le fichier. Une portion de console est donc d'abord écrite dans
  `%TEMP%\claude-vs-mcp\snippets\`, puis mentionnée.
- Les lignes y sont **0-based** alors qu'EnvDTE compte à partir de 1.

Claude Code associe une session à un IDE via `workspaceFolders`, republié à chaque changement de
solution. Si l'association automatique échoue — le terminal n'étant pas un processus enfant de
`devenv`, contrairement au terminal intégré de VS Code — `/ide` permet de choisir l'instance à la
main.

## Sécurité

- Écoute sur `127.0.0.1` uniquement — jamais `+` ni `0.0.0.0`. Ce choix évite aussi d'avoir besoin
  d'une `urlacl` ou de droits administrateur.
- Jeton obligatoire (`Authorization: Bearer …`), comparé à temps constant, stocké dans
  `%APPDATA%\claude-vs-mcp\token` et partagé par toutes les instances de l'utilisateur.
- En-têtes `Origin` et `Host` validés : sans cela, n'importe quelle page web ouverte dans le
  navigateur pourrait piloter Visual Studio par DNS rebinding.
- Corps de requête plafonné à 1 Mo.

**À garder en tête** : ce serveur donne de fait l'exécution de code arbitraire. Un build lance
les targets MSBuild et les scripts pre/post-build du dépôt, et le débogueur démarre des processus.
Il tourne avec vos privilèges.

## Limites connues

- **`get_errors` ne renvoie pas le code d'erreur** (`CS0103`…) : `EnvDTE.ErrorItem` ne l'expose pas.
  L'obtenir demanderait de passer par `IVsTaskList2` / `IVsTaskItem3.GetColumnValue`.
- **`get_stack` ne donne ni fichier ni ligne** : `EnvDTE.StackFrame` expose uniquement
  fonction, module, langage et type de retour. Pour la position courante, utiliser `debug_state`,
  qui la déduit du document actif — fiable en pratique, mais sensible à la navigation manuelle.
- **`evaluate` / `get_locals` / `get_stack` exigent le mode arrêt.** L'évaluateur d'expressions
  n'existe pas pendant l'exécution.
- **Évaluer peut avoir des effets de bord** : une propriété appelée pendant l'évaluation exécute
  son code.
- **`get_output_pane`** : le buffer d'un pane est borné par Visual Studio, le détail du pane de
  compilation dépend de la verbosité MSBuild, et la sortie d'une application console lancée dans
  une **console externe n'y apparaît pas du tout**.
- **Une boîte de dialogue modale fige l'automation.** Chaque outil a donc un timeout. Penser à
  activer le rechargement automatique des fichiers modifiés hors de l'éditeur, sinon chaque
  modification faite par Claude Code déclenche une popup.
- **Le Test Explorer n'a pas d'API publique** : passer par `dotnet test` en ligne de commande.
- Un seul appel d'outil s'exécute à la fois par instance : EnvDTE tolère mal la réentrance, et
  deux `debug_step` concurrents corrompraient l'état du débogueur.

## Versionnage

La version vit **à un seul endroit** : l'attribut `Version` de `<Identity>` dans
`src/ClaudeCodeVsMcp/source.extension.vsixmanifest`. `AssemblyVersion`, `AssemblyFileVersion` et
`McpDispatcher.ServerVersion` en sont dérivés à la compilation (cible MSBuild `GenerateVersionInfo`),
il n'y a donc jamais deux fichiers à tenir en phase.

Chaque modification s'accompagne d'un bump :

| Niveau | Quand |
|---|---|
| patch | correction ou ajustement interne, sans changement visible |
| mineur | nouvel outil MCP, nouvelle commande, nouvelle capacité |
| majeur | rupture pour un utilisateur existant : outil supprimé ou renommé, changement de port ou d'authentification |

Ce n'est pas qu'une convention d'historique : **le mécanisme de mise à jour de Visual Studio en
dépend**. `VSIXInstaller` ne met à jour en place que si la version du manifeste est supérieure à
celle installée. Une version figée oblige à désinstaller avant chaque réinstallation.

## Notes d'implémentation

Trois pièges structurent le code, et les enfreindre casse l'extension de façon silencieuse :

1. **Les objets d'événements COM doivent être conservés dans des champs.** `dte.Events.BuildEvents`
   renvoie un nouvel objet à chaque accès ; sans référence forte, le GC le collecte et les
   événements cessent d'arriver sans la moindre erreur.
2. **Jamais de surcharge bloquante de DTE** (`Build(true)`, `Go(true)`, `Step*(true)`) : le thread
   UI est celui dont le build et le débogueur ont besoin pour progresser. Toujours `false` +
   attente de l'événement, et jamais de `.Result` / `.Wait()` sur le thread UI.
3. **Les `TaskCompletionSource` utilisent `RunContinuationsAsynchronously`**, sinon la suite du
   traitement s'exécute en ligne dans le callback COM, sur le thread UI.

Autre point non évident : **les panes de la fenêtre Sortie sont localisés** (« Générer » sur un
IDE français). Ils sont donc résolus par GUID, jamais par nom.

Le protocole MCP est implémenté à la main : le SDK C# officiel cible `net8.0`/`netstandard2.0` et
sa variante serveur exige ASP.NET Core, inutilisable depuis un VSIX .NET Framework in-process.
La sérialisation passe par Newtonsoft.Json, fourni par le shell Visual Studio — l'assembly n'est
pas embarquée dans le VSIX, ce qui évite les conflits de redirection de liaison dans `devenv.exe`.

## Dépannage

| Symptôme | Piste |
|---|---|
| Rien dans la fenêtre Sortie | `devenv /log`, puis lire `%APPDATA%\Microsoft\VisualStudio\<version>\ActivityLog.xml` |
| `claude mcp list` échoue | Vérifier d'abord avec `tools\Smoke-Test.ps1` : isole transport et configuration client |
| Port 5230 déjà pris | `Get-NetTCPConnection -LocalPort 5230`, ou définir `CLAUDE_VS_MCP_HUB_PORT` |
| Mauvaise instance pilotée | `list_instances` puis `use_instance` |

## Licence

[PolyForm Noncommercial 1.0.0](LICENSE) — voir le fichier `LICENSE`.

Vous pouvez utiliser, modifier, redistribuer et forker ce code **à des fins non commerciales** :
usage personnel, étude, recherche, projet amateur, ou au sein d'une organisation à but non lucratif.

En revanche, **tout usage commercial est exclu** — et cela va plus loin que la simple revente :
une entreprise ne peut pas l'utiliser pour son activité, même en interne et même sans en tirer
directement de revenu. C'est le choix assumé de cette licence.

Deux points à connaître :

- PolyForm Noncommercial **n'est pas une licence open source** au sens de l'OSI. GitHub l'affichera
  comme « Other ». C'est inhérent à toute clause non commerciale, quelle qu'elle soit.
- Elle est en revanche **conçue pour du logiciel**, contrairement aux licences Creative Commons non
  commerciales, qui ne traitent ni le code source, ni les brevets, ni la garantie — Creative Commons
  déconseille elle-même leur usage pour du code.

Pour un usage commercial, contactez le détenteur des droits : rien n'empêche l'octroi d'une licence
distincte.
