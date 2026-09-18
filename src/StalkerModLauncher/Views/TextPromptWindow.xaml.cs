using System.Windows;
using System.Windows.Input;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.Views;

public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueTextBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            ValueTextBox.Focus();
            ValueTextBox.SelectAll();
        };
    }

    public string Value { get; private set; } = string.Empty;

    private void DoneButton_OnClick(object sender, RoutedEventArgs e) => Complete();

    private void ValueTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Complete();
            e.Handled = true;
        }
    }

    private void Complete()
    {
        var value = ValueTextBox.Text.Trim();
        if (value.Length == 0)
        {
            return;
        }

        Value = value;
        DialogResult = true;
    }

    private void Window_OnSourceInitialized(object? sender, EventArgs e) =>
        WindowSystemIntegrationService.Initialize(this);
}
