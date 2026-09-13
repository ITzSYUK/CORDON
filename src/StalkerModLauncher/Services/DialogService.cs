using System.Diagnostics;
using System.Media;
using Microsoft.Win32;
using System.Windows;

namespace StalkerModLauncher.Services;

public class DialogService
{
    internal event Action<string, string>? ErrorRequested;

    public static string? PickFolder(string description, string? initialPath = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = description,
            InitialDirectory = Directory.Exists(initialPath) ? initialPath : string.Empty,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public static string? PickExecutable(string title, string? initialPath = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(initialPath) ? initialPath : string.Empty,
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public static string? PickFile(string title, string filter, string? initialPath = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            InitialDirectory = Directory.Exists(initialPath) ? initialPath : string.Empty,
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public static bool Confirm(string title, string message)
    {
        return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    public virtual void ShowError(string title, string message)
    {
        if (ErrorRequested is { } handler)
        {
            SystemSounds.Hand.Play();
            handler(title, message);
            return;
        }

        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public static void ShowInfo(string title, string message)
    {
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(path);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = false
        });
    }

    public static void OpenFileLocation(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("File was not found.", path);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = false
        });
    }

    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Указан некорректный адрес сайта.", nameof(url));
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });
    }

    public static void CopyText(string text)
    {
        Clipboard.SetText(text);
    }
}
