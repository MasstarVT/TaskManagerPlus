using Microsoft.Win32;

namespace TaskManagerPlus.Common;

/// <summary>
/// The shell's file and folder pickers move the calling process's current directory to the folder
/// the user picked unless told not to - in this always-elevated app that is usually a user-writable
/// folder (Downloads, Desktop), which then sits in the search path of every later bare-name
/// CreateProcess/LoadLibrary. Save/Open dialogs opt out with <c>RestoreDirectory = true</c>;
/// <see cref="OpenFolderDialog"/> has no such property (it's FileDialog-only), so it goes through
/// this instead.
/// </summary>
public static class DialogExtensions
{
    public static bool? ShowDialogKeepingCurrentDirectory(this OpenFolderDialog dialog)
    {
        string currentDirectory = Environment.CurrentDirectory;
        try
        {
            return dialog.ShowDialog();
        }
        finally
        {
            try { Environment.CurrentDirectory = currentDirectory; } catch { /* folder gone - keep whatever the dialog left */ }
        }
    }
}
