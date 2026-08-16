// UseWindowsForms is on (the tray icon needs NotifyIcon), which puts System.Windows.Forms and
// System.Drawing into every file's implicit usings and makes a dozen type names ambiguous with
// their WPF counterparts. This is a WPF app, so WPF wins globally; the handful of places that
// genuinely want the WinForms/GDI+ type (TrayIconService, Screen lookups) qualify it explicitly.

global using Application = System.Windows.Application;
global using Binding = System.Windows.Data.Binding;
global using Brushes = System.Windows.Media.Brushes;
global using Color = System.Windows.Media.Color;
global using ColorConverter = System.Windows.Media.ColorConverter;
global using ExitEventArgs = System.Windows.ExitEventArgs;
global using FontFamily = System.Windows.Media.FontFamily;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using MessageBox = System.Windows.MessageBox;
global using MessageBoxButton = System.Windows.MessageBoxButton;
global using MessageBoxImage = System.Windows.MessageBoxImage;
global using MessageBoxResult = System.Windows.MessageBoxResult;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
global using Point = System.Windows.Point;
global using ShutdownMode = System.Windows.ShutdownMode;
global using StartupEventArgs = System.Windows.StartupEventArgs;
global using Window = System.Windows.Window;
global using WindowState = System.Windows.WindowState;
