using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Microsoft.Data.SqlClient;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;

namespace PlanViewer.App.Dialogs;

public partial class ConnectionDialog : Window
{
    private readonly ICredentialService _credentialService;
    private readonly ConnectionStore _connectionStore;
    private List<ServerConnection> _savedConnections = new();
    private Dictionary<string, ServerConnection> _byLabel = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeId;
    private string? _pendingDatabase;

    public ServerConnection? ResultConnection { get; private set; }
    public string? ResultDatabase { get; private set; }

    public ConnectionDialog(ICredentialService credentialService, ConnectionStore connectionStore,
        ServerConnection? initial = null, bool startBlank = false)
    {
        _credentialService = credentialService;
        _connectionStore = connectionStore;
        InitializeComponent();

        AuthTypeBox.SelectedIndex = 0;
        EncryptBox.SelectedIndex = 0;
        PopulateSavedServers(initial, startBlank);
    }

    private static string LabelFor(ServerConnection c) => c.DisplayName;

    private void PopulateSavedServers(ServerConnection? initial, bool startBlank)
    {
        RefreshList();

        if (initial != null)
        {
            ServerNameBox.Text = initial.ServerName;
            ApplySavedConnection(initial);
            return;
        }

        if (startBlank)
            return;

        // Pre-fill the most recently used connection
        var mostRecent = _savedConnections
            .OrderByDescending(s => s.LastConnected)
            .FirstOrDefault();

        if (mostRecent != null)
        {
            ServerNameBox.Text = mostRecent.ServerName;
            ApplySavedConnection(mostRecent);
        }
    }

    // Reloads saved connections from disk into both the autocomplete source
    // and the left-hand list, optionally re-selecting a specific entry.
    private void RefreshList(string? keepSelectedId = null)
    {
        _savedConnections = _connectionStore.Load();
        _byLabel = _savedConnections
            .GroupBy(LabelFor, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        ServerNameBox.ItemsSource = _byLabel.Keys.ToList();

        var ordered = _savedConnections.OrderByDescending(c => c.LastConnected).ToList();
        ConnectionsList.ItemsSource = ordered;

        if (keepSelectedId != null)
            ConnectionsList.SelectedItem = ordered.FirstOrDefault(c => c.Id == keepSelectedId);
    }

    private void ClearForm()
    {
        _activeId = null;
        _pendingDatabase = null;
        ServerNameBox.Text = "";
        AuthTypeBox.SelectedIndex = 0;
        EncryptBox.SelectedIndex = 0;
        TrustCertBox.IsChecked = false;
        LoginBox.Text = "";
        PasswordBox.Text = "";
        DatabaseBox.ItemsSource = null;
        DatabaseBox.IsEnabled = false;
        ConnectButton.IsEnabled = false;
        StatusText.Text = "";
        ConnectionsList.SelectedItem = null;
    }

    private void New_Click(object? sender, RoutedEventArgs e) => ClearForm();

    // When the user picks a saved connection from the left-hand list
    private void ConnectionsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ConnectionsList.SelectedItem is ServerConnection saved)
        {
            ServerNameBox.Text = saved.ServerName;
            ApplySavedConnection(saved);
        }
    }

    private async void Rename_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ServerConnection connection) return;

        var dialog = new RenameConnectionDialog(connection.DisplayName);
        var result = await dialog.ShowDialog<bool?>(this);
        if (result == true && dialog.ResultName != null)
        {
            connection.DisplayName = dialog.ResultName;
            connection.CustomDisplayName = true;
            _connectionStore.AddOrUpdate(connection);
            RefreshList(connection.Id);
        }
    }

    private void Duplicate_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ServerConnection connection) return;

        var clone = new ServerConnection
        {
            Id = Guid.NewGuid().ToString(),
            ServerName = connection.ServerName,
            DisplayName = connection.DisplayName,
            CustomDisplayName = connection.CustomDisplayName,
            AuthenticationType = connection.AuthenticationType,
            EncryptMode = connection.EncryptMode,
            TrustServerCertificate = connection.TrustServerCertificate,
            Database = connection.Database
        };

        var cred = _credentialService.GetCredential(connection.Id);
        if (cred != null)
            _credentialService.SaveCredential(clone.Id, cred.Value.Username, cred.Value.Password);

        _connectionStore.AddOrUpdate(clone);
        RefreshList(clone.Id);
    }

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ServerConnection connection) return;

        _connectionStore.Delete(connection.Id);
        _credentialService.DeleteCredential(connection.Id);

        if (_activeId == connection.Id)
        {
            ClearForm();
            RefreshList();
        }
        else
        {
            RefreshList(_activeId);
        }
    }

    private void Favorite_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as ToggleButton)?.Tag is not ServerConnection connection) return;

        connection.IsFavorite = !connection.IsFavorite;
        _connectionStore.AddOrUpdate(connection);
        RefreshList(connection.Id);
    }

    private void ApplySavedConnection(ServerConnection saved)
    {
        _activeId = saved.Id;
        _pendingDatabase = saved.Database;

        // Auth type
        for (int i = 0; i < AuthTypeBox.Items.Count; i++)
        {
            if (AuthTypeBox.Items[i] is ComboBoxItem item &&
                item.Tag?.ToString() == saved.AuthenticationType)
            {
                AuthTypeBox.SelectedIndex = i;
                break;
            }
        }

        // Encrypt mode
        for (int i = 0; i < EncryptBox.Items.Count; i++)
        {
            if (EncryptBox.Items[i] is ComboBoxItem item &&
                item.Tag?.ToString() == saved.EncryptMode)
            {
                EncryptBox.SelectedIndex = i;
                break;
            }
        }

        TrustCertBox.IsChecked = saved.TrustServerCertificate;

        // Load stored credentials
        var cred = _credentialService.GetCredential(saved.Id);
        if (cred != null)
        {
            LoginBox.Text = cred.Value.Username;
            PasswordBox.Text = cred.Value.Password;
        }

        // Prefill the saved database immediately so Connect doesn't require a
        // fresh Test Connection round-trip. Test Connection still refreshes
        // this with the live full database list.
        if (!string.IsNullOrEmpty(saved.Database))
        {
            DatabaseBox.ItemsSource = new List<string> { saved.Database };
            DatabaseBox.SelectedIndex = 0;
            DatabaseBox.IsEnabled = true;
            ConnectButton.IsEnabled = true;
            StatusText.Text = "Loaded saved database — Test Connection to verify or browse others.";
            StatusText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0xE4, 0xE6, 0xEB));
        }
        else
        {
            DatabaseBox.ItemsSource = null;
            DatabaseBox.IsEnabled = false;
            ConnectButton.IsEnabled = false;
            StatusText.Text = "";
        }

        var listMatch = _savedConnections.FirstOrDefault(s => s.Id == saved.Id);
        if (listMatch != null && !ReferenceEquals(ConnectionsList.SelectedItem, listMatch))
            ConnectionsList.SelectedItem = listMatch;
    }

    // When the user picks a saved connection from the autocomplete dropdown
    private void ServerName_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var label = ServerNameBox.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(label)) return;

        if (_byLabel.TryGetValue(label, out var saved))
        {
            ServerNameBox.Text = saved.ServerName;
            ApplySavedConnection(saved);
        }
    }

    // Typing over a previously-applied saved connection's server name means
    // the user wants a new/different profile, not to overwrite the applied one.
    private void ServerName_TextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        if (_activeId == null) return;

        var saved = _savedConnections.FirstOrDefault(s => s.Id == _activeId);
        if (saved == null || !string.Equals(ServerNameBox.Text?.Trim(), saved.ServerName, StringComparison.OrdinalIgnoreCase))
        {
            _activeId = null;
            _pendingDatabase = null;
        }
    }

    private void AuthType_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AuthTypeBox.SelectedItem is not ComboBoxItem item) return;
        var authType = item.Tag?.ToString();

        var showLogin = authType is "SqlServer" or "EntraMFA";
        var showPassword = authType == "SqlServer";

        LoginPanel.IsVisible = showLogin;
        PasswordPanel.IsVisible = showPassword;
    }

    private async void TestConnection_Click(object? sender, RoutedEventArgs e)
    {
        var serverName = ServerNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(serverName))
        {
            StatusText.Text = "Enter a server name";
            StatusText.Foreground = Avalonia.Media.Brushes.OrangeRed;
            return;
        }

        StatusText.Text = "Connecting...";
        StatusText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0xE4, 0xE6, 0xEB));
        TestButton.IsEnabled = false;

        try
        {
            var connection = BuildServerConnection();
            var connectionString = BuildConnectionString(connection);

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            // Fetch databases
            var databases = new List<string>();
            using var cmd = new SqlCommand(
                "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                databases.Add(reader.GetString(0));

            DatabaseBox.ItemsSource = databases;
            DatabaseBox.IsEnabled = true;
            ConnectButton.IsEnabled = true;

            // Prefer the saved profile's database, else default to master
            var pendingIdx = _pendingDatabase != null ? databases.IndexOf(_pendingDatabase) : -1;
            if (pendingIdx >= 0)
                DatabaseBox.SelectedIndex = pendingIdx;
            else
            {
                var masterIdx = databases.IndexOf("master");
                if (masterIdx >= 0) DatabaseBox.SelectedIndex = masterIdx;
            }

            StatusText.Text = $"Connected ({databases.Count} databases)";
            StatusText.Foreground = Avalonia.Media.Brushes.LimeGreen;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message.Length > 80 ? ex.Message[..80] + "..." : ex.Message;
            StatusText.Foreground = Avalonia.Media.Brushes.OrangeRed;
            DatabaseBox.IsEnabled = false;
            ConnectButton.IsEnabled = false;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Connect_Click(object? sender, RoutedEventArgs e)
    {
        var connection = BuildServerConnection();

        // Save credentials
        var authType = GetSelectedAuthType();
        if (authType == AuthenticationTypes.SqlServer)
        {
            var login = LoginBox.Text?.Trim() ?? "";
            var password = PasswordBox.Text ?? "";
            _credentialService.SaveCredential(connection.Id, login, password);
        }
        else if (authType == AuthenticationTypes.EntraMFA)
        {
            var login = LoginBox.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(login))
                _credentialService.SaveCredential(connection.Id, login, "");
        }

        // Save connection to store
        _connectionStore.AddOrUpdate(connection);

        ResultConnection = connection;
        ResultDatabase = DatabaseBox.SelectedItem?.ToString();
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private ServerConnection BuildServerConnection()
    {
        var serverName = ServerNameBox.Text?.Trim() ?? "";
        var database = DatabaseBox.SelectedItem?.ToString();
        var existing = _activeId != null
            ? _savedConnections.FirstOrDefault(s => s.Id == _activeId)
            : null;

        var customName = existing?.CustomDisplayName ?? false;
        var displayName = customName
            ? existing!.DisplayName
            : (string.IsNullOrEmpty(database) ? serverName : $"{serverName} ({database})");

        return new ServerConnection
        {
            Id = _activeId ?? Guid.NewGuid().ToString(),
            ServerName = serverName,
            DisplayName = displayName,
            CustomDisplayName = customName,
            AuthenticationType = GetSelectedAuthType(),
            TrustServerCertificate = TrustCertBox.IsChecked == true,
            EncryptMode = GetSelectedEncryptMode(),
            Database = database,
            CreatedDate = existing?.CreatedDate ?? DateTime.Now,
            IsFavorite = existing?.IsFavorite ?? false
        };
    }

    private string GetSelectedAuthType()
    {
        if (AuthTypeBox.SelectedItem is ComboBoxItem item)
            return item.Tag?.ToString() ?? AuthenticationTypes.Windows;
        return AuthenticationTypes.Windows;
    }

    private string GetSelectedEncryptMode()
    {
        if (EncryptBox.SelectedItem is ComboBoxItem item)
            return item.Tag?.ToString() ?? "Mandatory";
        return "Mandatory";
    }

    private string BuildConnectionString(ServerConnection connection)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = connection.ServerName,
            InitialCatalog = "master",
            ApplicationName = "PlanViewer",
            ConnectTimeout = 15,
            TrustServerCertificate = connection.TrustServerCertificate,
            Encrypt = connection.EncryptMode switch
            {
                "Optional" => SqlConnectionEncryptOption.Optional,
                "Strict" => SqlConnectionEncryptOption.Strict,
                _ => SqlConnectionEncryptOption.Mandatory
            }
        };

        switch (connection.AuthenticationType)
        {
            case AuthenticationTypes.SqlServer:
                builder.UserID = LoginBox.Text?.Trim() ?? "";
                builder.Password = PasswordBox.Text ?? "";
                break;
            case AuthenticationTypes.EntraMFA:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                if (!string.IsNullOrEmpty(LoginBox.Text?.Trim()))
                    builder.UserID = LoginBox.Text!.Trim();
                break;
            default:
                builder.IntegratedSecurity = true;
                break;
        }

        return builder.ConnectionString;
    }
}
