using System.Drawing;
using DevExpress.LookAndFeel;
using DevExpress.XtraEditors;

namespace ShinobiSave.Editor;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        WindowsFormsSettings.LoadApplicationSettings();

        // Same skin setup as OpkgStudio / Bo3Rtm: WXICompact / Darkness.
        UserLookAndFeel.Default.SetSkinStyle(SkinStyle.WXICompact, "Darkness");
        WindowsFormsSettings.DefaultLookAndFeel.UseWindowsXPTheme = false;
        WindowsFormsSettings.ForceDirectXPaint();
        WindowsFormsSettings.DefaultFont = new Font("Segoe UI", 9F);

        Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
    }
}
