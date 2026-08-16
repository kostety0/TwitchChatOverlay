using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace TwitchChatOverlay.UI.Settings;

/// <summary>Keeps MessageBox/file dialogs out of the view models.</summary>
public interface IUserDialogs
{
    bool Confirm(string message, string title);
    void Error(string message, string title);
    string? AskSavePath(string title, string defaultFileName);
    string? AskOpenPath(string title);
}

public sealed class UserDialogs : IUserDialogs
{
    private const string JsonFilter = "JSON (*.json)|*.json|Все файлы (*.*)|*.*";

    public bool Confirm(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void Error(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public string? AskSavePath(string title, string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = defaultFileName,
            Filter = JsonFilter,
            DefaultExt = ".json",
            AddExtension = true
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? AskOpenPath(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = JsonFilter,
            CheckFileExists = true
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
