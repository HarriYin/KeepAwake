using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Timer = System.Windows.Forms.Timer;

namespace KeepAwake
{
    internal sealed class TrayContext : ApplicationContext
    {
        // ---------- Win32 ----------
        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, IntPtr dwExtraInfo);

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;
        private const uint ES_AWAKE = ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED;
        private const uint MOUSEEVENTF_MOVE = 0x0001;

        private const int IdleThresholdSeconds = 60;   // must stay well below the 300s policy
        private const int CheckIntervalMs = 5000;

        private const string AppName = "KeepAwake";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        // LogicalName declared on the <EmbeddedResource> item in KeepAwake.csproj.
        private const string IconResourceLogicalName = "KeepAwake.AppIcon";
        private const string IconResourceFileName = "KeepAwake.ico";

        // ---------- state ----------
        private readonly NotifyIcon _tray;
        private readonly Timer _timer;
        private readonly Icon _iconOn;
        private readonly Icon _iconOff;
        // Bitmaps backing Icon.FromHandle(...) must outlive the Icon, so they are
        // kept alive here and disposed in Exit(). Null when the embedded icon is used.
        private Bitmap? _bmpOn;
        private Bitmap? _bmpOff;
        private Bitmap? _bmpDisabled;
        private readonly ToolStripMenuItem _itemStatus;
        private readonly ToolStripMenuItem _itemToggle;
        private readonly ToolStripMenuItem _itemAuto;

        private bool _enabled = true;
        private int _resetCount;

        public TrayContext()
        {
            Icon? embedded = TryLoadEmbeddedIcon();
            if (embedded is not null)
            {
                _iconOn = embedded;
                _bmpDisabled = CreateDisabledBitmap(embedded);
                _iconOff = Icon.FromHandle(_bmpDisabled.GetHicon());
            }
            else
            {
                // Fallback: generated dots, so the tray never shows a blank icon.
                _bmpOn = CreateDotBitmap(Color.FromArgb(76, 175, 80));
                _bmpOff = CreateDotBitmap(Color.FromArgb(120, 120, 120));
                _iconOn = Icon.FromHandle(_bmpOn.GetHicon());
                _iconOff = Icon.FromHandle(_bmpOff.GetHicon());
            }

            _itemStatus = new ToolStripMenuItem("状态: 运行中") { Enabled = false };

            _itemToggle = new ToolStripMenuItem("启用保持唤醒 (双击图标亦可切换)")
            {
                Checked = true
            };
            _itemToggle.Click += (s, e) => { SetEnabled(!_enabled); };

            _itemAuto = new ToolStripMenuItem("开机自动启动") { Checked = IsAutoStartEnabled() };
            _itemAuto.Click += (s, e) =>
            {
                try
                {
                    SetAutoStart(!_itemAuto.Checked);
                    _itemAuto.Checked = IsAutoStartEnabled();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("设置开机自启动失败: " + ex.Message, AppName,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            var itemExit = new ToolStripMenuItem("退出");
            itemExit.Click += (s, e) => Exit();

            var menu = new ContextMenuStrip();
            menu.Items.Add(_itemStatus);
            menu.Items.Add(_itemToggle);
            menu.Items.Add(_itemAuto);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(itemExit);

            _tray = new NotifyIcon
            {
                Icon = _iconOn,
                Visible = true,
                ContextMenuStrip = menu,
                Text = AppName + " - 运行中"
            };
            _tray.DoubleClick += (s, e) => { SetEnabled(!_enabled); };

            _timer = new Timer { Interval = CheckIntervalMs };
            _timer.Tick += OnTick;
            _timer.Start();

            SetThreadExecutionState(ES_AWAKE);
        }

        /// <summary>
        /// Loads KeepAwake.ico from the assembly manifest resources.
        /// Returns null when the resource is missing, never throws.
        /// </summary>
        private static Icon? TryLoadEmbeddedIcon()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();

                Stream? stream = asm.GetManifestResourceStream(IconResourceLogicalName);

                // Fallback: substring match, so a namespace rename cannot silently break it.
                if (stream is null)
                {
                    string? name = asm.GetManifestResourceNames()
                        .FirstOrDefault(n => n.IndexOf(IconResourceFileName, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (name is not null)
                        stream = asm.GetManifestResourceStream(name);
                }

                if (stream is null)
                    return null;

                using (stream)
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    buffer.Position = 0;

                    // 16x16 exists in the source .ico, so this picks that exact frame
                    // instead of resampling a larger one.
                    return new Icon(buffer, 16, 16);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Builds the "paused" variant: same artwork, desaturated and semi-transparent.
        /// </summary>
        private static Bitmap CreateDisabledBitmap(Icon source)
        {
            using Bitmap src = source.ToBitmap();

            var canvas = new Bitmap(src.Width, src.Height);

            var matrix = new ColorMatrix(new float[][]
            {
                new float[] { 0.30f, 0.30f, 0.30f, 0.00f, 0f },
                new float[] { 0.59f, 0.59f, 0.59f, 0.00f, 0f },
                new float[] { 0.11f, 0.11f, 0.11f, 0.00f, 0f },
                new float[] { 0.00f, 0.00f, 0.00f, 0.45f, 0f },
                new float[] { 0.00f, 0.00f, 0.00f, 0.00f, 1f }
            });

            using (var attr = new ImageAttributes())
            using (var g = Graphics.FromImage(canvas))
            {
                attr.SetColorMatrix(matrix);
                g.Clear(Color.Transparent);
                g.DrawImage(src,
                    new Rectangle(0, 0, src.Width, src.Height),
                    0, 0, src.Width, src.Height,
                    GraphicsUnit.Pixel, attr);
            }

            return canvas;
        }

        private static Bitmap CreateDotBitmap(Color color)
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var brush = new SolidBrush(color))
                {
                    g.FillEllipse(brush, 3, 3, 26, 26);
                }
                using (var pen = new Pen(Color.White, 2))
                {
                    g.DrawEllipse(pen, 3, 3, 26, 26);
                }
            }
            return bmp;
        }

        private static double GetIdleSeconds()
        {
            var lii = new LASTINPUTINFO();
            lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref lii))
                return -1;

            long now = Environment.TickCount;
            long last = lii.dwTime;
            long ms = now - last;
            if (ms < 0)
                ms = 0;
            return ms / 1000.0;
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (!_enabled)
                return;

            try
            {
                SetThreadExecutionState(ES_AWAKE);

                if (GetIdleSeconds() >= IdleThresholdSeconds)
                {
                    // real injected input - this is what resets GetLastInputInfo
                    mouse_event(MOUSEEVENTF_MOVE, 1, 0, 0, IntPtr.Zero);
                    Thread.Sleep(40);
                    mouse_event(MOUSEEVENTF_MOVE, -1, 0, 0, IntPtr.Zero);

                    _resetCount++;
                    UpdateTray();
                }
            }
            catch
            {
                // never let a transient failure kill the app
            }
        }

        private void SetEnabled(bool value)
        {
            _enabled = value;
            if (_enabled)
                SetThreadExecutionState(ES_AWAKE);
            UpdateTray();
        }

        private void UpdateTray()
        {
            _tray.Icon = _enabled ? _iconOn : _iconOff;
            _itemToggle.Checked = _enabled;

            if (_enabled)
            {
                _tray.Text = AppName + " - 运行中 (已阻止 " + _resetCount + " 次锁屏)";
                _itemStatus.Text = "状态: 运行中 - 已阻止 " + _resetCount + " 次";
            }
            else
            {
                _tray.Text = AppName + " - 已暂停";
                _itemStatus.Text = "状态: 已暂停";
            }
        }

        // ---------- autostart ----------
        private static bool IsAutoStartEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                return key != null && key.GetValue(AppName) != null;
            }
        }

        private static void SetAutoStart(bool enable)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key == null)
                    return;

                if (enable)
                {
                    string exe = Application.ExecutablePath;
                    key.SetValue(AppName, "\"" + exe + "\"");
                }
                else
                {
                    if (key.GetValue(AppName) != null)
                        key.DeleteValue(AppName, false);
                }
            }
        }

        private void Exit()
        {
            _timer.Stop();
            _tray.Visible = false;
            SetThreadExecutionState(ES_CONTINUOUS);
            _tray.Dispose();
            _iconOn.Dispose();
            _iconOff.Dispose();
            _bmpOn?.Dispose();
            _bmpOff?.Dispose();
            _bmpDisabled?.Dispose();
            ExitThread();
        }
    }
}
