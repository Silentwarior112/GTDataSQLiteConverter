using System.Text;

using GTParamDBEditor.UI;

namespace GTParamDBEditor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // euc-jp, used by the Japanese name columns, lives in the code pages provider.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        ApplicationConfiguration.Initialize();

        Application.ThreadException += (_, e) => ShowCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowCrash(e.ExceptionObject as Exception);

        Application.Run(new MainForm(args.FirstOrDefault(a => !a.StartsWith('-'))));
    }

    private static void ShowCrash(Exception? exception)
    {
        MessageBox.Show(
            $"Something went wrong and the editor could not recover.\n\n{exception}",
            "GT ParamDB Editor",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
