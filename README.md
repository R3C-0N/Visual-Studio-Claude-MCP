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

- « Compile et corrige les erreurs » → il compile, lit la liste d'erreurs structurée avec fichier,
  ligne et colonne, corrige, recompile.
- « Pose un point d'arrêt ligne 42 et dis-moi pourquoi `total` est négatif » → il pose le point,
  lance le débogage, attend l'arrêt, lit les variables locales et la pile d'appels.
- « Trace la valeur de `i` à chaque passage sans arrêter le programme » → il pose un tracepoint.
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
| Compilation | `build`, `build_status`, `cancel_build`, `get_errors` |
| Débogueur | `debug_start`, `debug_stop`, `debug_pause`, `debug_continue`, `debug_step`, `debug_state` |
| Points d'arrêt | `set_breakpoint`, `list_breakpoints`, `update_breakpoint`, `clear_breakpoints` |
| Inspection | `evaluate`, `get_locals`, `get_stack` |
| Sorties | `list_output_panes`, `get_output_pane` |
| Sélection | `get_selection`, `take_pending_context` |
| Instances | `list_instances`, `use_instance`, `server_info` |

Les opérations longues — compilation, exécution — acceptent un `wait_ms`. Au-delà, la réponse porte
le statut `running` : ce n'est pas une erreur mais un état à ré-interroger.

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

- **`get_errors` ne renvoie pas le code d'erreur** (`CS0103`…) : l'API d'automatisation ne l'expose
  pas.
- **`get_stack` ne donne ni fichier ni ligne**, seulement fonction, module et langage. Pour la
  position courante, utiliser `debug_state`.
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
- **Une boîte de dialogue modale fige l'automatisation.** Chaque outil a donc un délai maximal.
  Pensez à activer le rechargement automatique des fichiers modifiés hors de l'éditeur, sinon
  chaque modification faite par Claude ouvre une fenêtre.
- **Le Test Explorer n'a pas d'API publique** : passez par `dotnet test`.
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
