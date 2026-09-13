using LiveAssistant.Services;
using LiveAssistant.UI;

namespace LiveAssistant;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            MessageBox.Show($"程序异常: {e.Exception.Message}", "LiveAssistant", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        using var host = new LiveAppHost();
        Application.Run(new MainForm(host));
    }
}
