using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;

namespace TwitchChatOverlay.UI.Tray;

/// <summary>
/// Wraps a WinForms <see cref="NotifyIcon"/> — the app is tray-only, it has no WPF main window.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly ILogger<TrayIconService> _logger;
    private readonly NotifyIcon _notifyIcon;

    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? OpenConfigFolderRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService(ILogger<TrayIconService> logger)
    {
        _logger = logger;

        var menu = new ContextMenuStrip();
        var settingsItem = menu.Items.Add("Настройки");
        settingsItem.Font = new Font(menu.Font, FontStyle.Bold);
        settingsItem.Click += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
        var configItem = menu.Items.Add("Открыть папку конфигурации");
        configItem.Click += (_, _) => OpenConfigFolderRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(new ToolStripSeparator());
        var exitItem = menu.Items.Add("Выход");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = true,
            // NotifyIcon.Text is capped at 63 characters by the shell, so keep it short.
            Text = $"TwitchChatOverlay {GetVersion()}",
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string GetVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return string.Empty;
        }

        var plus = informational.IndexOf('+');
        return plus > 0 ? informational[..plus] : informational;
    }

    public void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _notifyIcon.ShowBalloonTip(3000, title, text, icon);
    }

    private Icon LoadIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "icons", "tray.ico");
        if (File.Exists(iconPath))
        {
            try
            {
                return new Icon(iconPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось загрузить иконку трея из {Path}, используется заглушка", iconPath);
            }
        }

        return GeneratePlaceholderIcon();
    }

    private static Icon GeneratePlaceholderIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var brush = new SolidBrush(System.Drawing.Color.FromArgb(255, 100, 65, 165));
            g.FillEllipse(brush, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(System.Drawing.Color.White);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("T", font, textBrush, new RectangleF(0, 0, 32, 32), format);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
