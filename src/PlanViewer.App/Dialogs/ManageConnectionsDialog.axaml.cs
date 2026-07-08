using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;

namespace PlanViewer.App.Dialogs;

public partial class ManageConnectionsDialog : Window
{
    private readonly ICredentialService _credentialService;
    private readonly ConnectionStore _connectionStore;

    public ManageConnectionsDialog(ICredentialService credentialService, ConnectionStore connectionStore)
    {
        _credentialService = credentialService;
        _connectionStore = connectionStore;
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        ConnectionsList.ItemsSource = _connectionStore.Load()
            .OrderByDescending(c => c.LastConnected)
            .ToList();
    }

    private async void New_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new ConnectionDialog(_credentialService, _connectionStore, startBlank: true);
        await dialog.ShowDialog<bool?>(this);
        Reload();
    }

    private async void Edit_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ServerConnection connection) return;

        var dialog = new ConnectionDialog(_credentialService, _connectionStore, initial: connection);
        await dialog.ShowDialog<bool?>(this);
        Reload();
    }

    private async void Duplicate_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ServerConnection connection) return;

        var clone = new ServerConnection
        {
            Id = Guid.NewGuid().ToString(),
            ServerName = connection.ServerName,
            DisplayName = connection.DisplayName,
            AuthenticationType = connection.AuthenticationType,
            EncryptMode = connection.EncryptMode,
            TrustServerCertificate = connection.TrustServerCertificate,
            Database = connection.Database
        };

        var cred = _credentialService.GetCredential(connection.Id);
        if (cred != null)
            _credentialService.SaveCredential(clone.Id, cred.Value.Username, cred.Value.Password);

        var dialog = new ConnectionDialog(_credentialService, _connectionStore, initial: clone);
        await dialog.ShowDialog<bool?>(this);
        Reload();
    }

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ServerConnection connection) return;

        _connectionStore.Delete(connection.Id);
        _credentialService.DeleteCredential(connection.Id);
        Reload();
    }

    private void Favorite_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as ToggleButton)?.Tag is not ServerConnection connection) return;

        connection.IsFavorite = !connection.IsFavorite;
        _connectionStore.AddOrUpdate(connection);
        Reload();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
