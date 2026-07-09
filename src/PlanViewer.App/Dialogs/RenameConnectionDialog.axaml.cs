using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PlanViewer.App.Dialogs;

public partial class RenameConnectionDialog : Window
{
    public string? ResultName { get; private set; }

    public RenameConnectionDialog(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Commit();

    private void NameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Commit();
    }

    private void Commit()
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        ResultName = name;
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
