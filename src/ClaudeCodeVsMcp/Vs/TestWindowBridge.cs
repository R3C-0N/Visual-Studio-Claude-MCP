using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Composition.Primitives;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVsMcp.Vs
{
    /// <summary>Un test tel que l'Explorateur de tests le connait.</summary>
    internal sealed class TestInfo
    {
        internal Guid Id { get; set; }
        internal string FullyQualifiedName { get; set; }
        internal string DisplayName { get; set; }
        internal string FilePath { get; set; }
        internal int LineNumber { get; set; }
        internal string Source { get; set; }

        /// <summary>Nom de TestState : None, Failed, Skipped, Passed, NotFound, NotRun.</summary>
        internal string State { get; set; }

        internal TimeSpan Duration { get; set; }
        internal bool Stale { get; set; }

        /// <summary>Plus grand ReceivedOrder des resultats, -1 sans resultat : sert a savoir si le test a tourne.</summary>
        internal long LastReceivedOrder { get; set; } = -1;

        internal List<TestResultInfo> Results { get; } = new List<TestResultInfo>();
    }

    internal sealed class TestResultInfo
    {
        internal string DisplayName { get; set; }
        internal string State { get; set; }
        internal string ErrorMessage { get; set; }
        internal string ErrorStackTrace { get; set; }
        internal string StandardOutput { get; set; }
        internal string StandardError { get; set; }
        internal TimeSpan Duration { get; set; }
        internal long ReceivedOrder { get; set; } = -1;
    }

    internal sealed class TestExplorerCounts
    {
        internal int Passed { get; set; }
        internal int Failed { get; set; }
        internal int Skipped { get; set; }
        internal int NotRun { get; set; }
    }

    /// <summary>
    /// Acces aux services MEF de l'Explorateur de tests (Microsoft.VisualStudio.TestWindow.*).
    ///
    /// Aucune API publique ne permet de lancer des tests et d'en lire les resultats : ITestsService,
    /// ITest, IResult, OperationBroker et SearchQuery sont internes. Tout passe donc par la
    /// reflexion, sans reference de compilation, et chaque membre introuvable leve UNSUPPORTED en
    /// le nommant : apres une mise a jour de Visual Studio, c'est par la qu'on saura quoi corriger.
    ///
    /// Les membres sont lus sur les types INTERFACE et non sur les classes concretes : plusieurs
    /// sont des implementations explicites (IResult.Outcome, IOperationState.StateChanged...),
    /// invisibles ou d'un autre type sur la classe.
    ///
    /// ITestsService.RunTestsAsync(IEnumerable&lt;Guid&gt;) n'est volontairement pas utilise : il
    /// compare les identifiants a des TestCaseId alors qu'ITest.Id est un TestCaseRecord.Id, et ne
    /// trouve donc jamais rien. Les executions passent par OperationBroker (export IOperationState).
    ///
    /// Toutes les methodes s'appellent sur le thread UI.
    /// </summary>
    internal sealed class TestWindowBridge
    {
        private const string Extensibility = "Microsoft.VisualStudio.TestWindow.Extensibility.";
        private const string TestsServiceContract = Extensibility + "ITestsService";
        private const string OperationStateContract = Extensibility + "IOperationState";
        private const string StatsServiceContract = Extensibility + "ITestExplorerStatsService";

        /// <summary>Package de l'Explorateur de tests (TestWindowPackage).</summary>
        private static readonly Guid TestWindowPackageGuid = new Guid("BFC24BF4-B994-4757-BCDC-1D5D2768BF29");

        /// <summary>Fenetre de l'Explorateur de tests (TestWindowToolWindow).</summary>
        private static readonly Guid TestWindowToolWindowGuid = new Guid("E1B7D1F8-9B3C-49B1-8F4F-BFC63A88835D");

        /// <summary>Propriete de filtre du nom complet, telle que la recherche de l'Explorateur l'emploie.</summary>
        private const string FullyQualifiedNameProperty = "TestWindow_FullyQualifiedName";

        private const int FilterMatchKindExactMatch = 2;
        private const int TestStateFailed = 1;

        private readonly TestRunWatcher _watcher;

        private bool _initialized;
        private object _testsService;
        private object _broker;
        private object _statsService;

        private MethodInfo _getTests;
        private Type _testType;
        private Type _resultType;

        internal TestWindowBridge(TestRunWatcher watcher)
        {
            _watcher = watcher;
        }

        /// <summary>
        /// Charge le package, cree la fenetre (sans quoi le magasin de tests et la decouverte ne
        /// demarrent pas) et resout les services. Un echec n'est pas memorise : l'appel suivant
        /// retente, par exemple une fois la solution chargee.
        /// </summary>
        internal void EnsureInitialized()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_initialized) return;

            DteProvider.RequireSolution();

            if (ServiceProvider.GlobalProvider.GetService(typeof(SVsShell)) is IVsShell shell)
            {
                var packageGuid = TestWindowPackageGuid;
                if (shell.IsPackageLoaded(ref packageGuid, out _) != VSConstants.S_OK)
                {
                    shell.LoadPackage(ref packageGuid, out _);
                }
            }

            if (ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell)) is IVsUIShell uiShell)
            {
                var windowGuid = TestWindowToolWindowGuid;
                if (uiShell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fForceCreate, ref windowGuid, out var frame) == VSConstants.S_OK)
                {
                    frame?.ShowNoActivate();
                }
            }

            var componentModel = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel;
            if (componentModel == null)
            {
                throw new McpToolException("UNSUPPORTED", "Le catalogue MEF de Visual Studio n'est pas disponible.");
            }

            var testsService = ResolveExport(componentModel, TestsServiceContract);
            var broker = ResolveExport(componentModel, OperationStateContract);
            if (testsService == null || broker == null)
            {
                throw new McpToolException("UNSUPPORTED",
                    "Les services de l'Explorateur de tests sont introuvables (" +
                    (testsService == null ? TestsServiceContract : OperationStateContract) +
                    "). La solution ne contient peut-etre aucun projet de test.");
            }

            var testsInterface = FindInterface(testsService.GetType(), TestsServiceContract);
            _getTests = testsInterface.GetMethod("GetTestsAsEnumerableAsync", Type.EmptyTypes)
                        ?? throw Missing("ITestsService.GetTestsAsEnumerableAsync()");

            var interfaces = testsInterface.Assembly;
            _testType = interfaces.GetType(Extensibility + "ITest") ?? throw Missing("ITest");
            _resultType = interfaces.GetType(Extensibility + "IResult") ?? throw Missing("IResult");

            _watcher.Attach(broker, FindInterface(broker.GetType(), OperationStateContract));

            _testsService = testsService;
            _broker = broker;
            _statsService = ResolveExport(componentModel, StatsServiceContract);
            _initialized = true;
        }

        /// <summary>Compteurs de l'Explorateur, ou null s'il n'a pas encore demarre.</summary>
        internal TestExplorerCounts GetCounts()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_statsService == null) return null;

            try
            {
                var contract = FindInterface(_statsService.GetType(), StatsServiceContract);
                if (contract.GetProperty("IsTestExplorerStatsServiceRunning")?.GetValue(_statsService) is bool running && !running)
                {
                    return null;
                }

                var stats = contract.GetProperty("TestExplorerStats")?.GetValue(_statsService);
                if (stats == null) return null;

                return new TestExplorerCounts
                {
                    Passed = ReadInt(stats, "PassedTestCount"),
                    Failed = ReadInt(stats, "FailedTestCount"),
                    Skipped = ReadInt(stats, "SkippedTestCount"),
                    NotRun = ReadInt(stats, "NotRunTestCount")
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Tous les tests decouverts, avec leurs derniers resultats.</summary>
        internal async Task<List<TestInfo>> GetTestsAsync()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnsureInitialized();

            var task = (Task)_getTests.Invoke(_testsService, null);
            await task.ConfigureAwait(true);

            var tests = new List<TestInfo>();
            if (!(TaskResult(task) is IEnumerable items)) return tests;

            foreach (var item in items)
            {
                if (item != null) tests.Add(ReadTest(item));
            }
            return tests;
        }

        internal Task StartRunAll()
        {
            return InvokeBroker("ExecuteAllTestsAsync", parameters => new object[] { null, CancellationToken.None });
        }

        internal Task StartRunByNames(IReadOnlyCollection<string> fullyQualifiedNames)
        {
            return InvokeBroker("ExecuteTestsByFilterAsync",
                parameters => new object[] { BuildNameFilter(parameters[0].ParameterType, fullyQualifiedNames), CancellationToken.None });
        }

        internal Task StartRunFailed()
        {
            return InvokeBroker("ExecuteTestsByOutcomeAsync", parameters =>
            {
                var states = (IList)Activator.CreateInstance(parameters[0].ParameterType);
                var stateType = parameters[0].ParameterType.GetGenericArguments()[0];
                states.Add(Enum.ToObject(stateType, TestStateFailed));
                return new object[] { states, null, CancellationToken.None };
            });
        }

        /// <summary>
        /// La tache renvoyee ne se termine qu'a la fin de la session de debogage : ne jamais
        /// l'attendre pendant un appel d'outil, le premier point d'arret la bloquerait.
        /// </summary>
        internal Task StartDebugByNames(IReadOnlyCollection<string> fullyQualifiedNames)
        {
            return InvokeBroker("DebugTestsByFilterAsync",
                parameters => new object[] { BuildNameFilter(parameters[0].ParameterType, fullyQualifiedNames), CancellationToken.None });
        }

        internal Task Cancel()
        {
            return InvokeBroker("CancelAsync", parameters => new object[] { CancellationToken.None });
        }

        /// <summary>
        /// Appelle une methode d'OperationBroker et renvoie sa tache sans l'attendre. La methode est
        /// choisie par son nom et son dernier parametre CancellationToken, les surcharges pouvant
        /// varier d'une version a l'autre.
        /// </summary>
        private Task InvokeBroker(string name, Func<ParameterInfo[], object[]> arguments)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnsureInitialized();

            var method = _broker.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == name &&
                                     m.GetParameters().LastOrDefault()?.ParameterType == typeof(CancellationToken))
                ?? throw Missing("OperationBroker." + name);

            return (Task)method.Invoke(_broker, arguments(method.GetParameters()));
        }

        /// <summary>
        /// List&lt;SearchQuery&gt; a une seule requete : nom complet egal a l'un des noms donnes.
        /// Le constructeur public de SearchQuery est (property, value, values, subQueries, matchKind).
        /// </summary>
        private static object BuildNameFilter(Type listType, IReadOnlyCollection<string> fullyQualifiedNames)
        {
            var queryType = listType.GetGenericArguments()[0];
            var constructor = queryType
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(c => c.GetParameters().Length == 5)
                ?? throw Missing("SearchQuery(property, value, values, subQueries, matchKind)");

            var matchKindType = constructor.GetParameters()[4].ParameterType;
            var query = constructor.Invoke(new object[]
            {
                FullyQualifiedNameProperty,
                null,
                new List<string>(fullyQualifiedNames),
                null,
                Enum.ToObject(matchKindType, FilterMatchKindExactMatch)
            });

            var list = (IList)Activator.CreateInstance(listType);
            list.Add(query);
            return list;
        }

        private TestInfo ReadTest(object test)
        {
            var info = new TestInfo
            {
                Id = Read(test, _testType, "Id") is Guid id ? id : Guid.Empty,
                FullyQualifiedName = Read(test, _testType, "FullyQualifiedName") as string,
                DisplayName = Read(test, _testType, "DisplayName") as string,
                FilePath = Read(test, _testType, "FilePath") as string,
                LineNumber = Read(test, _testType, "LineNumber") is int line ? line : 0,
                Source = Read(test, _testType, "Source") as string,
                State = Read(test, _testType, "State")?.ToString() ?? "NotRun",
                Duration = Read(test, _testType, "Duration") is TimeSpan duration ? duration : TimeSpan.Zero,
                Stale = Read(test, _testType, "Stale") is bool stale && stale
            };

            if (Read(test, _testType, "Results") is IEnumerable results)
            {
                foreach (var result in results)
                {
                    if (result == null) continue;

                    var order = Read(result, _resultType, "ReceivedOrder") is long received ? received : -1;
                    info.Results.Add(new TestResultInfo
                    {
                        DisplayName = Read(result, _resultType, "DisplayName") as string,
                        State = Read(result, _resultType, "State")?.ToString(),
                        ErrorMessage = Read(result, _resultType, "ErrorMessage") as string,
                        ErrorStackTrace = Read(result, _resultType, "ErrorStackTrace") as string,
                        StandardOutput = Read(result, _resultType, "StandardOutput") as string,
                        StandardError = Read(result, _resultType, "StandardError") as string,
                        Duration = Read(result, _resultType, "Duration") is TimeSpan d ? d : TimeSpan.Zero,
                        ReceivedOrder = order
                    });
                    info.LastReceivedOrder = Math.Max(info.LastReceivedOrder, order);
                }
            }

            return info;
        }

        private static object ResolveExport(IComponentModel componentModel, string contract)
        {
            // Filtre sur le seul nom de contrat : le type des exports est interne, on ne peut pas
            // le nommer dans un GetService<T>.
            var definition = new ImportDefinition(
                export => export.ContractName == contract, contract, ImportCardinality.ZeroOrMore, false, false);
            try
            {
                return componentModel.DefaultExportProvider.GetExports(definition).FirstOrDefault()?.Value;
            }
            catch (Exception ex)
            {
                ExtensionLog.Warn("Export MEF " + contract + " introuvable : " + ex.Message);
                return null;
            }
        }

        private static Type FindInterface(Type type, string fullName)
        {
            return type.GetInterfaces().FirstOrDefault(i => i.FullName == fullName)
                   ?? throw Missing(fullName + " sur " + type.FullName);
        }

        /// <summary>Lit une propriete de l'interface ou de l'une de ses interfaces de base ; null si absente.</summary>
        private static object Read(object instance, Type contract, string name)
        {
            var property = contract.GetProperty(name)
                           ?? contract.GetInterfaces().Select(i => i.GetProperty(name)).FirstOrDefault(p => p != null);
            if (property == null) return null;

            try { return property.GetValue(instance); }
            catch (Exception) { return null; }
        }

        private static int ReadInt(object instance, string name)
        {
            return instance.GetType().GetProperty(name)?.GetValue(instance) is int value ? value : 0;
        }

        private static object TaskResult(Task task)
        {
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }

        private static McpToolException Missing(string member)
        {
            return new McpToolException("UNSUPPORTED",
                "L'Explorateur de tests de cette version de Visual Studio n'expose plus " + member +
                ". Repli possible : execute_command (TestExplorer.RunAllTests...) et ui_snapshot.");
        }
    }
}
