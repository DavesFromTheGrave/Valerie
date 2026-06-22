using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Valerie
{
    public class HotKeyManager : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const int WM_HOTKEY = 0x0312;
        private int _currentId;
        private IntPtr _hWnd;
        private Action _onHotKeyPressed;

        // Modifiers
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

        public HotKeyManager(IntPtr hWnd, Action onHotKeyPressed)
        {
            _hWnd = hWnd;
            _onHotKeyPressed = onHotKeyPressed;
            _currentId = 0;
        }

        public void RegisterGlobalHotKey(uint modifiers, Keys key)
        {
            _currentId++;
            if (!RegisterHotKey(_hWnd, _currentId, modifiers, (uint)key))
            {
                Console.WriteLine("Warning: Could not register global hotkey.");
            }
        }

        public void HandleWndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                _onHotKeyPressed?.Invoke();
            }
        }

        public void Dispose()
        {
            UnregisterHotKey(_hWnd, _currentId);
        }
    }
}
