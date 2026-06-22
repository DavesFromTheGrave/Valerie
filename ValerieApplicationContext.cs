using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Valerie
{
    public class ValerieApplicationContext : ApplicationContext
    {
        private NotifyIcon _trayIcon;
        private OverlayForm _overlayForm;
        private Func<string, Task> _onUserInput;

        public ValerieApplicationContext(Func<string, Task> onUserInput)
        {
            _onUserInput = onUserInput;

            // Initialize Tray Icon
            _trayIcon = new NotifyIcon()
            {
                Icon = SystemIcons.Application, // Ideally load Valerie.ico here
                ContextMenuStrip = new ContextMenuStrip(),
                Visible = true,
                Text = "Valerie - System V"
            };

            try
            {
                if (System.IO.File.Exists("Valerie.ico"))
                {
                    _trayIcon.Icon = new Icon("Valerie.ico");
                }
            }
            catch { }

            _trayIcon.ContextMenuStrip.Items.Add("Show Logs", null, ShowLogs);
            _trayIcon.ContextMenuStrip.Items.Add("Exit", null, Exit);

            // Initialize Overlay Form
            _overlayForm = new OverlayForm(async (input) =>
            {
                await _onUserInput(input);
                _overlayForm.SetStatus("Valerie - Ready");
            });
            
            // Start Hidden
            _overlayForm.Hide();
        }

        private void ShowLogs(object? sender, EventArgs e)
        {
            try
            {
                string logDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "personal", "chats", "valerie");
                if (System.IO.Directory.Exists(logDir))
                {
                    System.Diagnostics.Process.Start("explorer.exe", logDir);
                }
            }
            catch { }
        }

        private void Exit(object? sender, EventArgs e)
        {
            _overlayForm.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            Application.Exit();
            Environment.Exit(0);
        }
    }
}
