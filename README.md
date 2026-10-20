# Claude Code ↔ Visual Studio

Pilotez Visual Studio depuis Claude Code : compiler, lire les erreurs, poser des points d'arrêt,
dérouler le débogueur pas à pas et inspecter les variables — sans quitter votre terminal.

L'extension héberge un serveur [MCP](https://modelcontextprotocol.io) dans Visual Studio et expose
l'IDE comme un jeu d'outils que Claude Code peut appeler.

**Visual Studio 2022 (17.x) et 2026 (18.x)** · projets **C# / .NET**

---

## Ce que ça change

Sans l'extension, Claude Code édite des fichiers et lance `dotnet build` à l'aveugle. Avec, il
travaille dans votre IDE :

- « Compile et corrige les erreurs » → il compile, lit la liste d'erreurs structurée avec code
  (`CS0103`…), fichier, ligne et colonne, corrige, recompile.
- « Pose un point d'arrêt ligne 42 et dis-moi pourquoi `total` est négatif » → il pose le point,
  lance le débogage, attend l'arrêt, lit les variables locales et la pile d'appels.
- « Trace la valeur de `i` à chaque passage sans arrêter le programme » → il pose un tracepoint.
- « D'où vient cette `InvalidOperationException` avalée par un `catch` ? » → il fait arrêter le
  débogueur à la levée et lit l'exception : type, message, fichier et ligne d'origine.
- « Qui appelle `Apply` ? » → il demande les références à Roslyn, qui analyse déjà la solution :
  les homonymes sont exclus et les surcharges distinguées, ce qu'une recherche de texte ne fait pas.
- « Renomme `Total` en `Montant` partout » ou « ajoute le using manquant » → il passe par Roslyn
  (`rename_symbol`, `apply_code_fix`) au lieu d'éditer du texte, et rien n'est appliqué si la
  modification casse la compilation.
- « Lance les tests de `OrderService` et dis-moi pourquoi ça échoue » → il les exécute dans
  l'Explorateur de tests et lit, test par test, le message d'erreur et la pile ; au besoin, il
  relance le test fautif sous le débogueur.
- « Le programme est figé, regarde » → il le met en pause et lit les piles de tous les threads,
  regroupées : un interblocage saute aux yeux.
- « Regarde ce que j'ai sélectionné » → il lit votre sélection dans l'éditeur ou la console.

C'est le contrôle du débogueur qui fait la différence : aucun outil en ligne de commande ne le
remplace.

---

## Installation

**1. Télécharger l'extension**

Récupérez `ClaudeCodeVsMcp.vsix` depuis la
[dernière release](https://github.com/R3C-0N/Visual-Studio-Claude-MCP/releases/latest).

**2. Installer**

Fermez Visual Studio, puis double-cliquez sur le `.vsix`.

**3. Enregistrer le serveur auprès de Claude Code**

Relancez Visual Studio et ouvrez une solution. Dans la fenêtre **Sortie**, choisissez
**Claude MCP** dans la liste « Afficher la sortie à partir de ». La commande à copier s'y trouve,
jeton compris :

```
claude mcp add --transport http visual-studio http://127.0.0.1:5230/mcp --header "Authorization: Bearer <votre-jeton>"
```

Le jeton est propre à votre machine et généré au premier lancement. Ajoutez `--scope user` pour
rendre le serveur disponible depuis n'importe quel répertoire, et pas seulement celui où vous
lancez la commande.

**4. Vérifier**

```powershell
claude mcp list        # visual-studio ... ✔ Connected
```

C'est tout. Demandez à Claude de compiler votre solution pour valider.

---

## Outils disponibles

| Domaine | Outils |
|---|---|
| Solution | `solution_info`, `list_projects`, `open_file`, `set_startup_project`, `set_configuration` |
| Navigation | `find_symbol`, `go_to_definition`, `find_references` |
| Refactoring | `rename_symbol`, `apply_code_fix` |
| Documents | `get_dirty_documents`, `save_documents` |
| Compilation | `build`, `build_status`, `cancel_build`, `get_errors` |
| Tests | `list_tests`, `run_tests`, `test_status`, `test_results`, `cancel_tests`, `debug_tests` |
| Débogueur | `debug_start`, `debug_stop`, `debug_pause`, `debug_continue`, `debug_step`, `debug_state`, `run_to_line`, `set_next_statement`, `debug_attach`, `debug_detach` |
| Threads | `list_threads`, `switch_thread`, `get_all_stacks` |
| Points d'arrêt | `set_breakpoint`, `list_breakpoints`, `update_breakpoint`, `clear_breakpoints` |
| Inspection | `evaluate`, `get_locals`, `get_stack` |
| Exceptions | `break_on_exception`, `get_exception` |
| Sorties | `list_output_panes`, `get_output_pane` |
| Sélection | `get_selection`, `take_pending_context` |
| Interface | `ui_windows`, `ui_snapshot`, `ui_action`, `execute_command` |
| Boîtes de dialogue | `get_dialog`, `answer_dialog` |
| Instances | `list_instances`, `use_instance`, `server_info` |

Les opérations longues — compilation, exécution — acceptent un `wait_ms`. Au-delà, la réponse porte
le statut `running` : ce n'est pas une erreur mais un état à ré-interroger.

`debug_stop` termine directement les processus débogués au lieu d'émettre la commande *Arrêter le
débogage* de Visual Studio, qui ouvre selon le projet une confirmation modale. Passer
`force: false` pour retrouver la commande d'origine — utile si la session est attachée à un
processus qu'il ne faut pas tuer. Si Visual Studio demande alors confirmation, `debug_stop` répond
**Oui** et le signale dans `confirmedDialogs` ; avec `confirm: false`, il rend la main avec le
statut `dialog_open` et le contenu de la boîte.

### Boîtes de dialogue modales

Tant qu'une boîte modale est ouverte, Visual Studio ne traite plus aucune commande. L'extension
les détecte sans passer par le thread UI qu'elles bloquent :

- les outils de débogage (`debug_start`, `debug_continue`, `debug_step`, `debug_pause`,
  `debug_stop`) rendent la main dès qu'une boîte apparaît, avec le statut `dialog_open`, au lieu
  d'attendre leur délai ;
- tout autre outil dont la commande reste bloquée par une boîte (une compilation lancée pendant
  le débogage ouvre « Voulez-vous arrêter le débogage ? ») rend la main en moins d'une seconde
  avec l'erreur `DIALOG_OPEN`, qui décrit la boîte. La commande reprend dès qu'on y a répondu ;
- `get_dialog` lit les boîtes ouvertes (titre, message, boutons) et `answer_dialog` clique un
  bouton par son libellé, son `id`, ou une réponse standard indépendante de la langue (`yes`,
  `no`, `ok`, `cancel`…).

Ces deux outils s'exécutent même quand un autre appel est en cours sur l'instance. Les boîtes
natives (MessageBox) sont lues par Win32, les boîtes WPF par UI Automation.

### Tests

Les outils de test passent par l'Explorateur de tests de Visual Studio, quel que soit le framework
(MSTest, xUnit, NUnit…), et rendent des résultats structurés au lieu du texte de la fenêtre :

```
list_tests   filter: "OrderService"                        → noms complets, état, fichier, ligne
run_tests    tests: ["MyApp.Tests.OrderServiceTests.Total"] → bilan : passed/failed/skipped/notRun
test_results outcome: "failed"                             → message d'erreur et pile de chaque échec
debug_tests  tests: ["MyApp.Tests.OrderServiceTests.Total"] → arrêt sur le point d'arrêt
```

- `run_tests` cible des noms complets (`tests`), un texte (`filter`, `project`), les échecs à
  rejouer (`failed_only`) ou, sans argument, tous les tests. L'Explorateur compile d'abord ce qui a
  changé : un build en échec donne le statut `not_started`, à lire avec `get_errors`.
- Comme pour `build`, au-delà de `wait_ms` la réponse porte le statut `running` ; `test_status`
  donne la phase (`building`, `running`…) puis le bilan. Il voit aussi les exécutions lancées à la
  main depuis l'Explorateur.
- Un test ciblé qui n'a pas tourné apparaît en `notRun` plutôt qu'avec son ancien résultat.
- `debug_tests` exige qu'aucune session de débogage ne soit en cours. Au statut `break`,
  `get_stack`, `get_locals` et `evaluate` s'utilisent comme d'habitude.

Ces outils s'appuient sur des services internes de l'Explorateur de tests, que Microsoft ne
documente pas (voir *Limites connues*). La démarche doit beaucoup à l'exploration publiée par
[VsDebugMcp](https://github.com/wuxinmao2008/VsDebugMcp) et
[VS-MCPServer](https://github.com/CodingWithCalvin/VS-MCPServer).

### Piloter n'importe quelle fenêtre

Les fenêtres sans API d'automatisation se pilotent par leur interface. `ui_snapshot` renvoie l'arbre des contrôles WPF en JSON : chaque nœud porte un `id`, son
type, son nom, sa valeur, son état (`toggle`, `expanded`, `selected`) et la liste des actions qu'il
accepte. `ui_action` applique une action à un `id` : `click`, `invoke`, `toggle`, `expand`,
`collapse`, `select`, `set_value`, `scroll_into_view`, `focus`.

```
ui_snapshot  window: "Solution Explorer"        → arbre : barre d'outils, arborescence de la solution
ui_action    id: 412, action: "expand"           → déplie un projet
ui_snapshot  root_id: 412                        → ses éléments, désormais matérialisés
ui_action    id: 418, action: "select"           → sélectionne un fichier
execute_command command: "Edit.OpenWith"
```

`ui_windows` liste les légendes des fenêtres ouvertes, pour savoir quoi passer à `window`. Par
défaut `interactive_only` ne garde que les nœuds nommés, valorisés ou actionnables et aplatit les
conteneurs muets ; `filter` réduit encore l'arbre aux nœuds contenant un texte. Les `id` restent
valables tant que le contrôle existe : après une action qui recompose la fenêtre, refaire un
snapshot. Les listes virtualisées n'exposent que les éléments affichés : déplier ou faire défiler,
puis reprendre un snapshot.

`execute_command` exécute une commande nommée de Visual Studio (`View.TestExplorer`,
`TestExplorer.RunAllTests`, `Edit.FormatDocument`…), ce qui évite souvent de passer par
l'interface.

---

## Points d'arrêt

`set_breakpoint` couvre cinq variantes, combinables entre elles :

| Variante | Paramètre | Comportement |
|---|---|---|
| Ordinaire | — | interrompt l'exécution |
| Conditionnel | `condition` | déclenche si l'expression est vraie, ou quand sa valeur change |
| Tracepoint | `message` | journalise **sans** interrompre |
| Temporaire | `temporary` | se supprime après son premier déclenchement |
| Dépendant | `depends_on` | reste désactivé jusqu'à ce qu'un autre point d'arrêt soit atteint |

S'y ajoutent `hit_count` avec son mode (`equal`, `greater_or_equal`, `multiple`) et `filter` pour
restreindre à un thread.

Le message d'un tracepoint accepte la syntaxe de Visual Studio — expressions entre accolades et
pseudo-variables :

```
i = {i}, appelé par $CALLER sur le thread $TID
```

Les points d'arrêt sont identifiés par `chemin:ligne`, format attendu par `depends_on` et
`update_breakpoint`.

---

## Exceptions

`break_on_exception` règle la fenêtre *Paramètres d'exception* :

| `break_when` | Comportement |
|---|---|
| `thrown` (défaut) | interrompt dès la levée, même si un `catch` la traite ensuite |
| `user_unhandled` | interrompt quand elle sort du code utilisateur sans être traitée (*Uniquement mon code*) |
| `never` | n'interrompt plus |

`exception` accepte un nom complet (`System.InvalidOperationException`), un nom court s'il est sans
ambiguïté (`NullReferenceException`), ou plusieurs noms séparés par des virgules. Un nom complet
inconnu — une exception du projet — est ajouté au groupe. Sans `exception`, l'outil liste celles qui
interrompent à la levée. `group` cible un autre groupe que .NET (`C++`, `Win32`…), `reset_all`
rétablit les réglages par défaut. Le réglage vaut pour la session en cours comme pour les suivantes.

Quand le débogueur s'arrête sur une exception, `debug_start` et `debug_continue` le signalent, et
`get_exception` la décrit :

```json
{
  "stoppedAt": "thrown",
  "type": "System.InvalidOperationException",
  "message": "Sequence contains no elements",
  "hresult": "0x80131509",
  "origin": { "method": "MyApp.Orders.Last(...)", "file": "C:\src\MyApp\Orders.cs", "line": 42 },
  "stackTrace": [ ... ],
  "innerExceptions": [ ... ]
}
```

La pile vient de `StackTrace` de l'exception, avec fichier et ligne quand les symboles sont
disponibles. Les `AggregateException` détaillent toutes leurs
exceptions internes (`aggregated`). Pour une exception native ou SEH, sans objet à inspecter,
`get_exception` rend le code et la description annoncés par le débogueur.

---

## Navigation dans le code

`find_symbol`, `go_to_definition` et `find_references` interrogent Roslyn sur la solution que
Visual Studio a déjà chargée : pas de second chargement, et le résultat est exact là où une recherche
de texte confond homonymes, surcharges et implémentations d'interface.

Un symbole se désigne de deux façons :

- **par une occurrence** : `file`, `line` et `symbol`, l'identifiant tel qu'il est écrit sur cette
  ligne. C'est la plus sûre, puisque Claude vient en général de lire le fichier ;
- **par son nom** : `name`, simple ou qualifié (`ExceptionBreaks.Apply`). S'il est ambigu, l'erreur
  liste les candidats.

```json
{ "file": "src/MyApp/Orders.cs", "line": 42, "symbol": "Last" }
```

Chaque résultat donne fichier, ligne, colonne, texte de la ligne et projet. `find_symbol` accepte un
motif (`ExBr` trouve `ExceptionBreaks`), `go_to_definition` rend toutes les parties d'un type
`partial` et peut ouvrir l'éditeur (`open`). Un symbole du framework ou d'un paquet NuGet renvoie
son assembly.

### Modifications non enregistrées

Un fichier modifié dans Visual Studio sans être enregistré diffère de ce que Claude lit sur le
disque. `get_dirty_documents` (éventuellement sur un seul `path`) les liste avant une modification,
`save_documents` les enregistre. Les résultats de navigation portent `unsaved: true` quand leurs
lignes viennent d'un tel tampon.

---

## Refactorings

`rename_symbol` renomme un symbole dans toute la solution, comme F2 ; il se désigne comme pour la
navigation (`file` + `line` + `symbol`, ou `name`). `apply_code_fix` reprend l'ampoule : sans `fix`,
il liste les diagnostics d'une ligne et les correctifs proposés ; avec `fix` (index ou partie du
titre), il applique celui choisi.

```json
{ "file": "src/MyApp/Orders.cs", "line": 12, "code": "CS0246", "fix": "using System.Text" }
```

Les deux outils suivent le même protocole :

- `preview: true` décrit chaque fichier touché, ligne par ligne (avant / après), sans rien écrire ;
- les erreurs de compilation que la modification **ajouterait** sont listées dans `newErrors`. S'il y
  en a, rien n'est appliqué, sauf avec `force: true` : c'est ce qui arrête un renommage vers un nom
  déjà pris ;
- un fichier partagé par plusieurs cibles d'un projet multi-cible reçoit la même modification
  partout.

Les correctifs viennent de Visual Studio (ajouter un `using`, implémenter une interface, règles
`IDE*`…) et des paquets d'analyseurs du projet.

---

## Threads et attachement

Quand le programme est arrêté, `get_all_stacks` donne la pile de chaque thread, en regroupant les
piles identiques comme la fenêtre *Piles parallèles* : cinquante threads du pool bloqués au même
endroit tiennent en une entrée, et deux threads qui s'attendent mutuellement ressortent aussitôt.
`list_threads` résume chaque thread par sa première frame de votre code, `switch_thread` en fait le
thread courant pour `get_stack`, `get_locals` et `evaluate`.

`run_to_line` exécute jusqu'à une ligne sans point d'arrêt ; `set_next_statement` déplace
l'instruction suivante dans la fonction courante, par exemple pour rejouer un bloc après avoir
corrigé une variable. `debug_attach` s'attache à un processus déjà lancé, par PID ou par nom
(`w3wp`, un service…), et `debug_detach` le laisse continuer sans débogueur.

---

## Envoyer une sélection depuis Visual Studio

Dans l'éditeur (`Ctrl+Alt+Maj+C` ou clic droit) et dans le menu contextuel de la fenêtre Sortie :
**« Envoyer à Claude Code »**. La sélection est figée au moment du clic, ce qui évite que Claude
lise autre chose au moment où vous formulez votre demande.

- Si Claude Code est connecté à l'IDE (`/ide` dans le terminal), la sélection est poussée
  directement dans le prompt.
- Sinon elle est mise en file, et Claude la récupère avec `take_pending_context`.

`get_selection` lit également la sélection courante à la demande, sans rien envoyer.

---

## Plusieurs instances de Visual Studio

Chaque instance s'enregistre, et Claude Code garde une URL unique : `http://127.0.0.1:5230/mcp`.

- Une seule instance ouverte → rien à faire.
- Plusieurs → `list_instances` pour les voir, `use_instance` pour choisir, ou le paramètre
  `instance` sur chaque appel.
- En cas d'ambiguïté, les outils **refusent de deviner** et renvoient la liste des choix : une
  compilation lancée dans la mauvaise solution coûte plus cher qu'une question.

Si l'instance qui sert de point d'entrée se ferme, une autre reprend le rôle en moins de dix
secondes, sans reconfiguration.

---

## Ne plus jamais avoir à se reconnecter : le relais stdio

Claude Code ne retente une connexion HTTP que brièvement : 3 essais à l'ouverture de la session,
environ 30 secondes après une coupure. Passé ce délai, le serveur est marqué en échec jusqu'à
un `/mcp` → **Reconnect** manuel. C'est ce qui arrive si Claude Code démarre avant Visual Studio,
ou si Visual Studio est fermé un moment.

`tools\vs-mcp-relay.mjs` règle ça. Claude Code le lance comme un serveur **stdio**, donc
toujours connecté, et le relais transmet chaque appel au hub au moment où il arrive :

- sans Visual Studio, un appel d'outil attend jusqu'à 30 s, ce qui couvre une reprise de hub,
  puis répond « Visual Studio n'est pas joignable » au lieu de faire échouer le serveur ;
- la liste d'outils est gardée en cache et servie même quand Visual Studio est fermé ;
- dès que Visual Studio revient, le relais prévient Claude Code (`tools/list_changed`) : il n'y a
  rien à faire.

Le jeton est relu dans `%APPDATA%\claude-vs-mcp\token` à chaque requête, il n'apparaît donc plus
dans la configuration. Node 18 ou plus, aucune dépendance.

```powershell
claude mcp remove visual-studio --scope user
claude mcp add visual-studio --scope user -- node "<chemin>\tools\vs-mcp-relay.mjs"
```

| Variable | Rôle | Défaut |
|---|---|---|
| `CLAUDE_VS_MCP_URL` | URL du hub | `http://127.0.0.1:<port>/mcp` |
| `CLAUDE_VS_MCP_HUB_PORT` | Port du hub, si l'URL n'est pas donnée | `5230` |
| `CLAUDE_VS_MCP_TOKEN` | Jeton, à la place du fichier | relu dans `%APPDATA%` |
| `CLAUDE_VS_MCP_WAIT_MS` | Attente maximale de Visual Studio par appel d'outil | `30000` |

Le journal du relais part sur stderr, dans les logs MCP de Claude Code.

---

## Sécurité

- Écoute sur `127.0.0.1` uniquement, jamais sur une interface externe.
- Jeton obligatoire, comparé à temps constant, stocké dans `%APPDATA%\claude-vs-mcp\token`.
- En-têtes `Origin` et `Host` validés : sans cela, une page web ouverte dans votre navigateur
  pourrait piloter Visual Studio par DNS rebinding.

**À garder en tête** : ce serveur donne de fait l'exécution de code arbitraire. Une compilation
lance les targets MSBuild et les scripts pre/post-build du dépôt ouvert, et le débogueur démarre
des processus. Il tourne avec vos privilèges. Ne l'utilisez pas sur une machine où vous ouvrez du
code auquel vous ne faites pas confiance.

---

## Limites connues

- **La navigation ne couvre que C# et VB** : Roslyn n'analyse pas le C++. Sur un fichier modifié
  non enregistré, les positions suivent le tampon de l'éditeur, pas le disque.
- **Fichier et ligne d'une frame exigent les symboles** (`get_stack`, `debug_state`…) : sans PDB,
  une frame n'a que sa fonction et son module.
- **Visual Studio passe au premier plan** à chaque arrêt du débogueur et quand un outil place le
  curseur (`run_to_line`, `set_next_statement`) : les outils rendent aussitôt la main à la fenêtre
  que vous utilisiez, mais une frappe tombée dans cet intervalle peut atterrir dans l'éditeur.
- **Les piles sont celles des threads**, pas les piles logiques `async` : une tâche en attente sans
  thread n'apparaît pas dans `get_all_stacks`.
- **`set_next_statement` reste dans la fonction courante**, et ce qui est sauté ou rejoué ne
  s'annule pas : un effet de bord rejoué s'exécute deux fois.
- **`apply_code_fix` ne propose que des correctifs de diagnostics** : les refactorings de l'ampoule
  sans diagnostic (extraire une méthode…) n'y figurent pas. `rename_symbol` ne renomme pas le
  fichier d'un type renommé.
- **`get_exception` évalue `Message`, `StackTrace`…** sur l'exception, donc du code du programme
  débogué : une exception personnalisée qui surcharge `Message` l'exécute.
- **`evaluate`, `get_locals` et `get_stack` exigent un arrêt** sur point d'arrêt : l'évaluateur
  d'expressions n'existe pas pendant l'exécution. Évaluer peut avoir des effets de bord, une
  propriété appelée exécutant son code.
- **La condition d'un point d'arrêt n'est pas modifiable** après création. `update_breakpoint` le
  recrée dans ce cas, ce qui remet son compteur de passages à zéro.
- **Points temporaires et dépendants sont émulés** par l'extension, Visual Studio ne les exposant
  pas : ils ne survivent pas à un rechargement de la solution et n'apparaissent pas comme tels dans
  la fenêtre Points d'arrêt.
- **La sortie d'une application console lancée dans une console externe** n'apparaît pas dans
  `get_output_pane`.
- **Une boîte de dialogue modale fige l'automatisation** tant qu'on n'y a pas répondu
  (`answer_dialog`). Les boîtes de dialogue de tâche (*TaskDialog*) ne laissent lire ni leur
  texte ni leurs boutons : `answer_dialog` y tente les réponses standard à l'aveugle. Pensez à activer le rechargement automatique des fichiers modifiés hors de l'éditeur, sinon
  chaque modification faite par Claude ouvre une fenêtre.
- **Les outils de test reposent sur des API internes** de l'Explorateur de tests (`ITestsService`,
  `OperationBroker`), lues par réflexion : une mise à jour de Visual Studio peut les casser. Ils
  répondent alors `UNSUPPORTED` en nommant le membre disparu ; en attendant un correctif,
  `execute_command` (`TestExplorer.RunAllTests`…) et `ui_snapshot` restent possibles.
- **Les tests ne sont connus qu'après leur découverte**, qui suit le build : sur une solution
  fraîchement ouverte, `list_tests` peut répondre `discovering` ou une liste vide.
- **`ui_snapshot` ne voit que l'interface WPF** : les zones Win32 ou WinForms hébergées et les
  boîtes de dialogue natives n'y apparaissent pas. Les éléments non affichés d'une liste
  virtualisée non plus.
- Un seul outil s'exécute à la fois par instance : l'automatisation de Visual Studio tolère mal la
  réentrance.

---

## Dépannage

| Symptôme | Piste |
|---|---|
| Rien dans le pane « Claude MCP » | `devenv /log`, puis lire `%APPDATA%\Microsoft\VisualStudio\<version>\ActivityLog.xml` |
| `claude mcp list` échoue | Vérifier d'abord avec `tools\Smoke-Test.ps1` : isole le transport de la configuration client |
| Port 5230 déjà pris | `Get-NetTCPConnection -LocalPort 5230`, ou définir `CLAUDE_VS_MCP_HUB_PORT` |
| Mauvaise instance pilotée | `list_instances` puis `use_instance` |

Le journal de l'extension est écrit dans le pane **Claude MCP** et dupliqué dans
`%TEMP%\claude-vs-mcp\extension-<pid>.log`.

---

## Développement

```powershell
pwsh tools\Build.ps1              # compile, produit le .vsix
pwsh tools\Build.ps1 -Install     # installe (Visual Studio fermé, terminal élevé)
pwsh tools\Smoke-Test.ps1         # teste le serveur en HTTP brut, sans Claude Code
pwsh tools\New-Release.ps1        # étiquette, pousse et publie une release
```

`F5` déploie dans la ruche expérimentale (`/rootsuffix Exp`) sans toucher à votre installation
principale.

**Hooks git** — à activer une fois par clone, `core.hooksPath` étant une configuration locale :

```powershell
git config core.hooksPath .githooks
```

`pre-commit` et `pre-push` refusent toute adresse e-mail non publique. Motif surchargeable via
`git config hooks.allowedEmail`.

**Versionnage** — la version vit à un seul endroit, l'attribut `Version` de
`source.extension.vsixmanifest` ; `AssemblyVersion` et la version annoncée par le serveur en sont
dérivées à la compilation. Chaque modification l'incrémente : patch pour une correction, mineur
pour une nouvelle capacité, majeur pour une rupture. Le mécanisme de mise à jour de Visual Studio
en dépend, `VSIXInstaller` ne remplaçant en place que si la version augmente.

### Trois pièges à connaître avant de contribuer

1. **Les objets d'événements COM doivent être gardés dans des champs.** `dte.Events.BuildEvents`
   renvoie un nouvel objet à chaque accès ; sans référence forte, le ramasse-miettes le collecte et
   les événements cessent d'arriver sans la moindre erreur.
2. **Jamais de surcharge bloquante** (`Build(true)`, `Go(true)`) : le thread UI est celui dont la
   compilation et le débogueur ont besoin pour progresser. Toujours `false` puis attente de
   l'événement, et jamais de `.Result` sur le thread UI.
3. **Les panes de sortie sont localisés** — « Générer » sur un IDE français. Ils sont résolus par
   GUID, jamais par nom.

---

## Licence

[Apache License 2.0 avec Commons Clause](LICENSE).

Utilisation, modification, redistribution et fork **libres**, y compris en entreprise et pour un
usage professionnel. La seule chose interdite est de **vendre** le logiciel au sens du Commons
Clause : fournir à des tiers, contre rémunération, un produit ou un service dont la valeur dérive
entièrement ou substantiellement de ses fonctionnalités — ce qui couvre la revente, l'hébergement
payant et le support facturé sur cette base.

Cette combinaison n'est pas une licence open source au sens de l'OSI, restriction commerciale
oblige. Pour un usage sortant de ce cadre, contactez le détenteur des droits.
