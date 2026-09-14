using LiveAssistant.Services;
using LiveAssistant.UI;

namespace LiveAssistant;

static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            MessageBox.Show($"程序异常: {e.Exception.Message}", AppBranding.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            MessageBox.Show($"程序崩溃: {ex?.Message ?? e.ExceptionObject}", AppBranding.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        bool createdNew;
        try
        {
            _singleInstance = new Mutex(true, @"Global\LiveAssistant.SingleInstance", out createdNew);
        }
        catch
        {
            _singleInstance = new Mutex(true, @"Local\LiveAssistant.SingleInstance", out createdNew);
        }

        if (!createdNew)
        {
            MessageBox.Show($"{AppBranding.DisplayName} 已在运行，无需重复打开。", AppBranding.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            PackagedContent.EnsureExtracted();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"释放内置配置失败: {ex.Message}", AppBranding.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        try
        {
            SidecarBootstrap.EnsureReady();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"补齐 Sidecar 文件失败: {ex.Message}", AppBranding.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        LiveAppHost? host = null;
        try
        {
            host = new LiveAppHost();
            var missing = host.GetMissingSidecarFiles();
            if (missing.Count > 0)
            {
                var fileList = string.Join(Environment.NewLine, missing.Select(f => $"  • {f}"));
                MessageBox.Show(
                    $"缺少必要文件，请放到程序同目录后重新打开：{Environment.NewLine}{Environment.NewLine}{fileList}",
                    AppBranding.DisplayName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            Application.Run(new MainForm(host));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败: {ex.Message}\n\n{ex}", AppBranding.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            try { host?.Dispose(); } catch { /* ignore */ }
            try
            {
                _singleInstance?.ReleaseMutex();
                _singleInstance?.Dispose();
            }
            catch { /* ignore */ }
        }
    }
}
