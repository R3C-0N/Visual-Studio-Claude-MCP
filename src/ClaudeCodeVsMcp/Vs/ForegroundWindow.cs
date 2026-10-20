using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeCodeVsMcp.Vs
{
    /// <summary>
    /// Fenetre au premier plan. Visual Studio se met au premier plan quand on active un document
    /// et a chaque arret du debogueur : ce que l'utilisateur tapait ailleurs a ce moment atterrit
    /// alors dans l'editeur. Les outils notent donc la fenetre active avant d'agir et la lui rendent
    /// ensuite, sauf si c'etait deja Visual Studio.
    ///
    /// Utilisable hors du thread UI : seules des API Win32 sont appelees.
    /// </summary>
    internal static class ForegroundWindow
    {
        internal static IntPtr Capture()
        {
            return GetForegroundWindow();
        }

        internal static void Restore(IntPtr previous)
        {
            if (previous == IntPtr.Zero || GetForegroundWindow() == previous) return;

            GetWindowThreadProcessId(previous, out var owner);
            if (owner == (uint)Process.GetCurrentProcess().Id) return;

            SetForegroundWindow(previous);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    }
}
