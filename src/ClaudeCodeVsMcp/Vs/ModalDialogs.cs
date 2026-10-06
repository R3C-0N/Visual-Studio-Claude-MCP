using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Vs
{
    /// <summary>
    /// Detection et reponse aux boites de dialogue modales de Visual Studio.
    ///
    /// Une boite modale fait tourner sa propre boucle de messages sur le thread UI : tant
    /// qu'elle est ouverte, la commande qui l'a ouverte ne rend pas la main et les appels
    /// marshales par UiThread attendent. Rien ici ne passe donc par UiThread :
    /// - boites natives (MessageBox, #32770) : Win32 pur. InternalGetWindowText lit le texte
    ///   sans envoyer de message, et la reponse est postee, pas envoyee ;
    /// - boites WPF : client UI Automation, appele depuis un thread d'arriere-plan. La boucle
    ///   modale pompe les messages, le provider repond ; chaque appel a quand meme un delai.
    ///
    /// Une fenetre est consideree modale si elle est visible, active, et que sa proprietaire
    /// est desactivee : c'est precisement ce que fait une boucle modale a sa fenetre parente.
    /// </summary>
    internal static class ModalDialogs
    {
        internal sealed class Button
        {
            internal int Id;
            internal string Label;
            internal IntPtr Handle;
            internal AutomationElement Element;
        }

        internal sealed class Dialog
        {
            internal IntPtr Handle;
            internal string Title;
            internal string Kind;
            internal string Message;
            internal List<Button> Buttons = new List<Button>();

            internal JObject ToJson()
            {
                var json = new JObject
                {
                    ["title"] = Title,
                    ["kind"] = Kind
                };
                if (!string.IsNullOrEmpty(Message)) json["message"] = Message;

                var buttons = new JArray();
                foreach (var button in Buttons)
                {
                    buttons.Add(new JObject { ["id"] = button.Id, ["label"] = button.Label });
                }
                json["buttons"] = buttons;

                if (Buttons.Count == 0)
                {
                    json["note"] = "Libelles des boutons illisibles. answer_dialog accepte les reponses " +
                                   "standard (yes, no, ok, cancel), tentees a l'aveugle.";
                }

                return json;
            }
        }

        private const int AutomationTimeoutMs = 3000;

        /// <summary>Premier identifiant attribue aux boutons WPF, qui n'en ont pas.</summary>
        private const int WpfButtonBaseId = 1000;

        /// <summary>Identifiants Win32 standard, identiques quelle que soit la langue de l'IDE.</summary>
        private static readonly Dictionary<string, int> StandardAnswers =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["ok"] = IDOK,
                ["cancel"] = IDCANCEL,
                ["abort"] = IDABORT,
                ["retry"] = IDRETRY,
                ["ignore"] = IDIGNORE,
                ["yes"] = IDYES,
                ["no"] = IDNO
            };

        /// <summary>
        /// Libelles des reponses standard, pour les boites WPF dont les boutons n'ont pas
        /// d'identifiant Win32. Anglais et francais, les deux langues d'IDE rencontrees.
        /// </summary>
        private static readonly Dictionary<string, string[]> StandardLabels =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["ok"] = new[] { "ok" },
                ["cancel"] = new[] { "cancel", "annuler" },
                ["abort"] = new[] { "abort", "abandonner" },
                ["retry"] = new[] { "retry", "réessayer", "recommencer" },
                ["ignore"] = new[] { "ignore", "ignorer" },
                ["yes"] = new[] { "yes", "oui" },
                ["no"] = new[] { "no", "non" }
            };

        /// <summary>Reponses tentees, dans l'ordre, pour confirmer une boite.</summary>
        private static readonly string[] AffirmativeAnswers = { "yes", "ok" };

        /// <summary>Boites modales ouvertes, la plus haute dans l'ordre Z en premier.</summary>
        internal static List<Dialog> FindAll()
        {
            var pid = (uint)Process.GetCurrentProcess().Id;
            var handles = new List<IntPtr>();

            // EnumWindows parcourt les fenetres de premier niveau du haut vers le bas de l'ordre Z.
            EnumWindows((hwnd, _) =>
            {
                if (IsModal(hwnd, pid)) handles.Add(hwnd);
                return true;
            }, IntPtr.Zero);

            var dialogs = new List<Dialog>();
            foreach (var hwnd in handles)
            {
                try
                {
                    dialogs.Add(Read(hwnd));
                }
                catch (Exception)
                {
                    // Fenetre detruite pendant la lecture.
                }
            }
            return dialogs;
        }

        /// <summary>Boite modale au premier plan, ou null.</summary>
        internal static Dialog FindTop()
        {
            return FindAll().FirstOrDefault();
        }

        /// <summary>Description courte pour un message d'erreur, ou null sans boite ouverte.</summary>
        internal static string Describe()
        {
            try
            {
                var dialog = FindTop();
                return dialog == null ? null : Describe(dialog);
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static string Describe(Dialog dialog)
        {
            var text = "Boite de dialogue ouverte : \"" + dialog.Title + "\"";
            if (!string.IsNullOrEmpty(dialog.Message)) text += " - " + dialog.Message;
            if (dialog.Buttons.Count > 0)
            {
                text += " [" + string.Join(", ", dialog.Buttons.Select(b => b.Label)) + "]";
            }
            return text + ". La lire avec get_dialog, y repondre avec answer_dialog.";
        }

        /// <summary>
        /// Repond par le bouton affirmatif (Oui, sinon OK). Renvoie le bouton clique, ou null si
        /// la boite n'en a pas ou ne s'est pas fermee.
        /// </summary>
        internal static Button Confirm(Dialog dialog)
        {
            foreach (var answer in AffirmativeAnswers)
            {
                var button = Resolve(dialog, answer, null);
                if (button != null && Click(dialog, button)) return button;
            }

            return null;
        }

        /// <summary>
        /// Trouve le bouton designe par un libelle, une reponse standard (yes, no, ok, cancel...)
        /// ou un identifiant. Si les boutons sont illisibles, construit un bouton a l'aveugle
        /// d'apres l'identifiant standard. Null si rien ne correspond.
        /// </summary>
        internal static Button Resolve(Dialog dialog, string answer, int? id)
        {
            if (id.HasValue)
            {
                return dialog.Buttons.FirstOrDefault(b => b.Id == id.Value)
                    ?? (dialog.Buttons.Count == 0 ? new Button { Id = id.Value, Label = id.Value.ToString() } : null);
            }

            if (string.IsNullOrWhiteSpace(answer)) return null;

            var wanted = Normalize(answer);
            var byLabel = dialog.Buttons.FirstOrDefault(b => Normalize(b.Label) == wanted);
            if (byLabel != null) return byLabel;

            int standardId;
            if (!StandardAnswers.TryGetValue(answer.Trim(), out standardId)) return null;

            var byId = dialog.Buttons.FirstOrDefault(b => b.Id == standardId);
            if (byId != null) return byId;

            var labels = StandardLabels[answer.Trim()];
            var byStandardLabel = dialog.Buttons.FirstOrDefault(b => labels.Contains(Normalize(b.Label)));
            if (byStandardLabel != null) return byStandardLabel;

            return dialog.Buttons.Count == 0 ? new Button { Id = standardId, Label = answer.Trim() } : null;
        }

        /// <summary>
        /// Clique un bouton et attend la fermeture de la boite. Les messages Win32 sont postes :
        /// un envoi synchrone attendrait que la boucle modale le traite, depuis un thread qui ne
        /// doit jamais attendre le thread UI.
        /// </summary>
        internal static bool Click(Dialog dialog, Button button, int closeTimeoutMs = 3000)
        {
            if (!IsWindow(dialog.Handle)) return true;

            if (button.Element != null)
            {
                // InvokePattern d'un bouton WPF : le peer poste le clic et rend la main aussitot.
                var invoked = WithTimeout(() =>
                {
                    ((InvokePattern)button.Element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    return true;
                });
                if (!invoked) return false;
            }
            else if (dialog.Kind == "task_dialog")
            {
                PostMessage(dialog.Handle, TDM_CLICK_BUTTON, (IntPtr)button.Id, IntPtr.Zero);
            }
            else
            {
                // WM_COMMAND est ce qu'emet un bouton clique. Sans handle (bouton illisible), une
                // boite WPF ou native le traite comme un raccourci : IDOK/IDCANCEL en general.
                PostMessage(dialog.Handle, WM_COMMAND,
                    (IntPtr)((BN_CLICKED << 16) | (button.Id & 0xFFFF)), button.Handle);
            }

            var deadline = Environment.TickCount + closeTimeoutMs;
            while (Environment.TickCount - deadline < 0)
            {
                if (!IsWindow(dialog.Handle) || !IsWindowVisible(dialog.Handle)) return true;
                Thread.Sleep(50);
            }

            return false;
        }

        // ------------------------------------------------------------------ lecture

        private static bool IsModal(IntPtr hwnd, uint pid)
        {
            uint owningPid;
            GetWindowThreadProcessId(hwnd, out owningPid);
            if (owningPid != pid) return false;

            if (!IsWindowVisible(hwnd) || !IsWindowEnabled(hwnd)) return false;

            var owner = GetWindow(hwnd, GW_OWNER);
            return owner != IntPtr.Zero && !IsWindowEnabled(owner);
        }

        private static Dialog Read(IntPtr hwnd)
        {
            var dialog = new Dialog
            {
                Handle = hwnd,
                Title = TextOf(hwnd),
                Kind = ClassOf(hwnd).StartsWith("HwndWrapper", StringComparison.Ordinal) ? "wpf" : "message_box"
            };

            if (dialog.Kind == "wpf")
            {
                ReadWpf(dialog);
                return dialog;
            }

            var lines = new List<string>();
            EnumChildWindows(hwnd, (child, _) =>
            {
                var childClass = ClassOf(child);

                if (childClass == "DirectUIHWND")
                {
                    dialog.Kind = "task_dialog";
                }
                else if (childClass == "Button" && IsWindowVisible(child) && IsPushButton(child))
                {
                    dialog.Buttons.Add(new Button
                    {
                        Id = GetDlgCtrlID(child),
                        Label = StripMnemonic(TextOf(child)),
                        Handle = child
                    });
                }
                else if (childClass == "Static" && IsWindowVisible(child))
                {
                    var text = TextOf(child);
                    if (!string.IsNullOrWhiteSpace(text)) lines.Add(text.Trim());
                }

                return true;
            }, IntPtr.Zero);

            dialog.Message = string.Join("\n", lines);
            return dialog;
        }

        /// <summary>
        /// Textes et boutons d'une boite WPF, par UI Automation. En cas d'echec ou de delai, la
        /// boite reste decrite par son seul titre : la detection prime sur le detail.
        /// </summary>
        private static void ReadWpf(Dialog dialog)
        {
            WithTimeout(() =>
            {
                var root = AutomationElement.FromHandle(dialog.Handle);
                var lines = new List<string>();
                var nextId = WpfButtonBaseId;

                var condition = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

                foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, condition))
                {
                    var current = element.Current;
                    if (current.IsOffscreen || string.IsNullOrWhiteSpace(current.Name)) continue;

                    var parentType = ParentType(element);

                    if (current.ControlType == ControlType.Text)
                    {
                        // Le libelle d'un bouton est lui-meme un texte : deja dans Buttons.
                        if (parentType != ControlType.Button) lines.Add(current.Name.Trim());
                    }
                    else if (current.IsEnabled && parentType != ControlType.TitleBar)
                    {
                        dialog.Buttons.Add(new Button
                        {
                            Id = nextId++,
                            Label = StripMnemonic(current.Name),
                            Element = element
                        });
                    }
                }

                dialog.Message = string.Join("\n", lines.Distinct());
                return true;
            });
        }

        /// <summary>
        /// Type du parent : distingue le libelle d'un bouton d'un texte de la boite, et les
        /// boutons Reduire / Fermer de la barre de titre, qui ne sont pas des reponses.
        /// </summary>
        private static ControlType ParentType(AutomationElement element)
        {
            var parent = TreeWalker.ControlViewWalker.GetParent(element);
            return parent?.Current.ControlType;
        }

        /// <summary>
        /// Appel UI Automation borne dans le temps : si le thread UI ne pompe pas, le provider ne
        /// repond pas et l'appel attendrait le delai interne d'UIA, bien plus long.
        /// </summary>
        private static bool WithTimeout(Func<bool> body)
        {
            try
            {
                // Attente synchrone voulue : appele hors du thread UI, depuis l'enumeration Win32.
#pragma warning disable VSTHRD002
                var task = Task.Run(body);
                return task.Wait(AutomationTimeoutMs) && task.Result;
#pragma warning restore VSTHRD002
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsPushButton(IntPtr hwnd)
        {
            var style = GetWindowLong(hwnd, GWL_STYLE).ToInt64() & BS_TYPEMASK;
            return style == BS_PUSHBUTTON || style == BS_DEFPUSHBUTTON;
        }

        /// <summary>InternalGetWindowText : aucun message envoye, donc aucune attente du thread UI.</summary>
        private static string TextOf(IntPtr hwnd)
        {
            var buffer = new StringBuilder(1024);
            InternalGetWindowText(hwnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        private static string ClassOf(IntPtr hwnd)
        {
            var buffer = new StringBuilder(256);
            GetClassName(hwnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        private static string StripMnemonic(string label)
        {
            // WPF marque le raccourci par '_', Win32 par '&'.
            return (label ?? string.Empty).Replace("&&", "\u0001").Replace("&", string.Empty).Replace("\u0001", "&")
                .Replace("__", "\u0001").Replace("_", string.Empty).Replace("\u0001", "_");
        }

        private static string Normalize(string label)
        {
            return StripMnemonic(label).Trim().ToLowerInvariant();
        }

        // ------------------------------------------------------------------ Win32

        private const int IDOK = 1;
        private const int IDCANCEL = 2;
        private const int IDABORT = 3;
        private const int IDRETRY = 4;
        private const int IDIGNORE = 5;
        private const int IDYES = 6;
        private const int IDNO = 7;

        private const uint GW_OWNER = 4;
        private const int GWL_STYLE = -16;
        private const long BS_TYPEMASK = 0xF;
        private const long BS_PUSHBUTTON = 0x0;
        private const long BS_DEFPUSHBUTTON = 0x1;
        private const int BN_CLICKED = 0;
        private const uint WM_COMMAND = 0x0111;
        private const uint WM_USER = 0x0400;
        private const uint TDM_CLICK_BUTTON = WM_USER + 102;

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

        [DllImport("user32.dll")]
        private static extern int GetDlgCtrlID(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int InternalGetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    }
}
