using System;
using System.Drawing;
using System.Windows.Forms;

namespace Valerie
{
    public class OverlayForm : Form
    {
        private TextBox _inputBox;
        private Label _statusLabel;
        private Action<string> _onSubmit;
        private HotKeyManager _hotKeyManager;

        public OverlayForm(Action<string> onSubmit)
        {
            _onSubmit = onSubmit;
            
            // Basic sleek overlay styling
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Size = new Size(600, 80);
            this.BackColor = Color.FromArgb(28, 28, 30); // Sleek dark gray
            this.Opacity = 0.95;
            this.TopMost = true;
            this.ShowInTaskbar = false;

            // Status label (e.g. "V is thinking...")
            _statusLabel = new Label
            {
                Text = "Valerie - Ready",
                ForeColor = Color.FromArgb(140, 140, 150),
                Font = new Font("Segoe UI", 9, FontStyle.Regular),
                Location = new Point(10, 5),
                AutoSize = true
            };
            this.Controls.Add(_statusLabel);

            // Input Box
            _inputBox = new TextBox
            {
                Location = new Point(10, 30),
                Width = 580,
                Font = new Font("Segoe UI", 14, FontStyle.Regular),
                BackColor = Color.FromArgb(40, 40, 42),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None
            };
            
            // Add slight padding to textbox via a wrapper panel if we wanted, but this is fine for now
            _inputBox.KeyDown += InputBox_KeyDown;
            this.Controls.Add(_inputBox);

            // Setup HotKey
            _hotKeyManager = new HotKeyManager(this.Handle, ToggleVisibility);
            // Ctrl + Space
            _hotKeyManager.RegisterGlobalHotKey(HotKeyManager.MOD_CONTROL, Keys.Space);
        }

        public void SetStatus(string status)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => _statusLabel.Text = status));
            }
            else
            {
                _statusLabel.Text = status;
            }
        }

        private void ToggleVisibility()
        {
            if (this.Visible)
            {
                this.Hide();
            }
            else
            {
                this.Show();
                this.Activate();
                _inputBox.Focus();
                _inputBox.Clear();
            }
        }

        private void InputBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                this.Hide();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                string text = _inputBox.Text.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    _inputBox.Clear();
                    this.Hide();
                    SetStatus("Thinking...");
                    _onSubmit?.Invoke(text);
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        protected override void WndProc(ref Message m)
        {
            _hotKeyManager?.HandleWndProc(ref m);
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hotKeyManager?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
