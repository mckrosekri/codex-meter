using System.Diagnostics;
using CodexDecision.Core.Integrations;
using CodexMeterTray.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexMeterTray.Views;

public partial class IntegrationsPage : Page
{
    private readonly AppServices services = ((App)Application.Current).Services;
    private readonly CodexCliIntegrationService cliIntegrations = new();
    private readonly SkillFileManager skillFiles = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly CancellationTokenSource pageLifetime = new();
    private IReadOnlyList<SkillIntegrationRow> allSkills = [];
    private IReadOnlyList<PluginIntegrationRow> allPlugins = [];
    private IReadOnlyList<McpIntegrationRow> allMcpServers = [];
    private bool pageReady;

    public IntegrationsPage()
    {
        InitializeComponent();
        pageReady = true;
        Loaded += async (_, _) => await RefreshAsync();
        Unloaded += (_, _) => pageLifetime.Cancel();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync(forceSkills: true);

    private async void ReloadMcpButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync(reloadMcp: true);

    private void OpenConfigButton_Click(object sender, RoutedEventArgs e) => OpenCodexConfig();

    private void SkillSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
        ApplySkillFilter();

    private void PluginSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
        ApplyPluginFilter();

    private void PluginFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyPluginFilter();

    private void McpSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
        ApplyMcpFilter();

    private void BrowsePluginsButton_Click(object sender, RoutedEventArgs e)
    {
        PluginFilterBox.SelectedIndex = 2;
        PluginSearchBox.Focus(FocusState.Programmatic);
    }

    private async void NewSkillButton_Click(object sender, RoutedEventArgs e)
    {
        var draft = await ShowSkillEditorAsync();
        if (draft is null)
        {
            return;
        }

        await RunMutationAsync(
            token => skillFiles.SaveAsync(draft, cancellationToken: token),
            "Creating personal skill…",
            $"Created ${draft.Name}. Codex will discover it automatically.",
            forceSkills: true);
    }

    private async void EditSkillButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not SkillIntegrationRow row || !row.CanEdit)
        {
            return;
        }

        try
        {
            var instructions = await skillFiles.ReadInstructionsAsync(row.Path, pageLifetime.Token);
            var draft = await ShowSkillEditorAsync(row, instructions);
            if (draft is null)
            {
                return;
            }

            await RunMutationAsync(
                token => skillFiles.SaveAsync(draft, row.Path, token),
                $"Saving {row.DisplayName}…",
                $"Saved ${row.Name}.",
                forceSkills: true);
        }
        catch (Exception error)
        {
            ShowError("Skill could not be opened", error.Message);
        }
    }

    private async void SkillToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not SkillIntegrationRow row)
        {
            return;
        }

        var enabled = !row.Enabled;
        await RunMutationAsync(
            token => services.Backend.SetSkillEnabledAsync(row.Path, enabled, token),
            $"{(enabled ? "Enabling" : "Disabling")} {row.DisplayName}…",
            $"{row.DisplayName} is now {(enabled ? "enabled" : "disabled")}.",
            forceSkills: true);
    }

    private async void RemoveSkillButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not SkillIntegrationRow row || !row.CanEdit)
        {
            return;
        }

        var confirmed = await ConfirmAsync(
            $"Remove {row.DisplayName}?",
            "Its entire personal skill folder will be moved to Codex trash, where it can be recovered manually.",
            "Move to trash");
        if (!confirmed)
        {
            return;
        }

        string? trashPath = null;
        await RunMutationAsync(
            async token => trashPath = await skillFiles.MoveToTrashAsync(row.Path, token),
            $"Removing {row.DisplayName}…",
            "Personal skill moved to Codex trash.",
            forceSkills: true);
        if (!string.IsNullOrWhiteSpace(trashPath))
        {
            ShowStatus("Skill removed", $"Moved to {trashPath}", InfoBarSeverity.Success);
        }
    }

    private async void PluginActionButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not PluginIntegrationRow row || !row.CanManage)
        {
            return;
        }

        if (row.Installed)
        {
            var confirmed = await ConfirmAsync(
                $"Uninstall {row.DisplayName}?",
                "Its bundled skills and tools will stop being available to new tasks. Connected external accounts are not disconnected.",
                "Uninstall");
            if (!confirmed)
            {
                return;
            }

            await RunMutationAsync(
                token => services.Backend.UninstallPluginAsync(row.Id, token),
                $"Uninstalling {row.DisplayName}…",
                $"Uninstalled {row.DisplayName}.");
            return;
        }

        await RunMutationAsync(
            token => services.Backend.InstallPluginAsync(
                row.Name,
                row.MarketplacePath,
                string.IsNullOrWhiteSpace(row.MarketplacePath) ? row.MarketplaceName : null,
                token),
            $"Installing {row.DisplayName}…",
            $"Installed {row.DisplayName}. Start a new task before using its skills or tools.");
    }

    private async void AddMcpButton_Click(object sender, RoutedEventArgs e)
    {
        var draft = await ShowMcpEditorAsync();
        if (draft is null)
        {
            return;
        }

        await RunMutationAsync(
            async token =>
            {
                await cliIntegrations.AddOrUpdateMcpServerAsync(draft, token);
                await services.Backend.ReloadMcpServersAsync(token);
            },
            "Adding MCP server…",
            $"Added {draft.Name} and reloaded MCP configuration.",
            reloadMcpInventory: true);
    }

    private async void EditMcpButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not McpIntegrationRow row || !row.CanEdit)
        {
            return;
        }

        var draft = await ShowMcpEditorAsync(row);
        if (draft is null)
        {
            return;
        }

        await RunMutationAsync(
            async token =>
            {
                await cliIntegrations.AddOrUpdateMcpServerAsync(draft, token);
                await services.Backend.ReloadMcpServersAsync(token);
            },
            $"Saving {row.Name}…",
            $"Updated {row.Name} and reloaded MCP configuration.",
            reloadMcpInventory: true);
    }

    private async void RemoveMcpButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not McpIntegrationRow row)
        {
            return;
        }

        var confirmed = await ConfirmAsync(
            $"Remove {row.Name}?",
            "This deletes the MCP server entry from Codex configuration. The server software itself is not removed.",
            "Remove server");
        if (!confirmed)
        {
            return;
        }

        await RunMutationAsync(
            async token =>
            {
                await cliIntegrations.RemoveMcpServerAsync(row.Name, token);
                await services.Backend.ReloadMcpServersAsync(token);
            },
            $"Removing {row.Name}…",
            $"Removed {row.Name}.",
            reloadMcpInventory: true);
    }

    private async Task RefreshAsync(bool forceSkills = false, bool reloadMcp = false)
    {
        if (!await refreshGate.WaitAsync(0))
        {
            return;
        }

        SetBusy(true, reloadMcp ? "Reloading MCP servers…" : "Refreshing integrations…");
        try
        {
            if (reloadMcp)
            {
                await services.Backend.ReloadMcpServersAsync(pageLifetime.Token);
            }

            IReadOnlyList<string> roots = [];
            var skillsTask = services.Backend.ListSkillsAsync(roots, forceSkills, pageLifetime.Token);
            var pluginsTask = services.Backend.ListPluginsAsync(roots, pageLifetime.Token);
            var mcpTask = cliIntegrations.ListMcpServersAsync(pageLifetime.Token);
            await Task.WhenAll(skillsTask, pluginsTask, mcpTask);

            allSkills = IntegrationInventoryParser.ParseSkills(await skillsTask)
                .Select(skill => new SkillIntegrationRow(skill, skillFiles.CanEdit(skill.Path)))
                .ToArray();
            allPlugins = IntegrationInventoryParser.ParsePlugins(await pluginsTask)
                .Select(plugin => new PluginIntegrationRow(plugin))
                .ToArray();
            allMcpServers = (await mcpTask)
                .Select(server => new McpIntegrationRow(server))
                .ToArray();
            SkillsTab.Header = $"Skills ({allSkills.Count})";
            PluginsTab.Header = $"Plugins ({allPlugins.Count})";
            McpTab.Header = $"MCP servers ({allMcpServers.Count})";
            ApplySkillFilter();
            ApplyPluginFilter();
            ApplyMcpFilter();
            StatusInfoBar.IsOpen = false;
        }
        catch (OperationCanceledException) when (pageLifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            ShowError("Integrations could not be refreshed", error.Message);
        }
        finally
        {
            SetBusy(false);
            refreshGate.Release();
        }
    }

    private async Task RunMutationAsync(
        Func<CancellationToken, Task> operation,
        string busyMessage,
        string successMessage,
        bool forceSkills = false,
        bool reloadMcpInventory = false)
    {
        if (!await operationGate.WaitAsync(0))
        {
            return;
        }

        var succeeded = false;
        SetBusy(true, busyMessage);
        try
        {
            await operation(pageLifetime.Token);
            succeeded = true;
        }
        catch (OperationCanceledException) when (pageLifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            ShowError("Integration change failed", error.Message);
        }
        finally
        {
            SetBusy(false);
            operationGate.Release();
        }

        if (!succeeded)
        {
            return;
        }

        await RefreshAsync(forceSkills, reloadMcpInventory);
        ShowStatus("Integrations updated", successMessage, InfoBarSeverity.Success);
    }

    private void ApplySkillFilter()
    {
        if (!pageReady)
        {
            return;
        }

        var query = SkillSearchBox.Text.Trim();
        var visible = allSkills.Where(skill =>
                Contains(skill.DisplayName, query) ||
                Contains(skill.Name, query) ||
                Contains(skill.Description, query) ||
                Contains(skill.Scope, query))
            .ToArray();
        SkillsList.ItemsSource = visible;
        SkillsEmptyState.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SkillsCountText.Text = CountLabel(visible.Length, allSkills.Count, "skill");
    }

    private void ApplyPluginFilter()
    {
        if (!pageReady)
        {
            return;
        }

        var query = PluginSearchBox.Text.Trim();
        var filter = (PluginFilterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        var visible = allPlugins.Where(plugin =>
                (filter == "All" ||
                 filter == "Installed" && plugin.Installed ||
                 filter == "Available" && !plugin.Installed) &&
                (Contains(plugin.DisplayName, query) ||
                 Contains(plugin.Name, query) ||
                 Contains(plugin.Description, query) ||
                 Contains(plugin.MarketplaceDisplayName, query)))
            .ToArray();
        PluginsList.ItemsSource = visible;
        PluginsEmptyState.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PluginsCountText.Text = CountLabel(visible.Length, allPlugins.Count, "plugin");
    }

    private void ApplyMcpFilter()
    {
        if (!pageReady)
        {
            return;
        }

        var query = McpSearchBox.Text.Trim();
        var visible = allMcpServers.Where(server =>
                Contains(server.Name, query) ||
                Contains(server.Endpoint, query) ||
                Contains(server.TransportLabel, query))
            .ToArray();
        McpList.ItemsSource = visible;
        McpEmptyState.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        McpCountText.Text = CountLabel(visible.Length, allMcpServers.Count, "server");
    }

    private async Task<SkillDraft?> ShowSkillEditorAsync(
        SkillIntegrationRow? existing = null,
        string instructions = "")
    {
        var nameBox = new TextBox
        {
            Header = "Name",
            PlaceholderText = "weekly-status",
            Text = existing?.Name ?? string.Empty,
            IsEnabled = existing is null,
            MaxLength = 64,
        };
        var descriptionBox = new TextBox
        {
            Header = "When should Codex use it?",
            PlaceholderText = "Prepare a concise weekly project update from supplied notes.",
            Text = existing?.Description ?? string.Empty,
            MaxLength = 500,
        };
        var instructionsBox = new TextBox
        {
            Header = "Instructions",
            PlaceholderText = "Describe the workflow, checks, and expected output.",
            Text = instructions,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 210,
        };
        var errorText = CreateDialogErrorText();
        var panel = new StackPanel { Spacing = 12, MinWidth = 320, MaxWidth = 560 };
        panel.Children.Add(nameBox);
        panel.Children.Add(descriptionBox);
        panel.Children.Add(instructionsBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = existing is null ? "Create personal skill" : $"Edit {existing.DisplayName}",
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 560,
            },
            PrimaryButtonText = existing is null ? "Create" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        SkillDraft? draft = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                draft = new SkillDraft(nameBox.Text.Trim(), descriptionBox.Text.Trim(), instructionsBox.Text.Trim());
                SkillFileManager.Validate(draft);
                errorText.Visibility = Visibility.Collapsed;
            }
            catch (Exception error)
            {
                args.Cancel = true;
                errorText.Text = error.Message;
                errorText.Visibility = Visibility.Visible;
            }
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? draft : null;
    }

    private async Task<McpServerDraft?> ShowMcpEditorAsync(McpIntegrationRow? existing = null)
    {
        var nameBox = new TextBox
        {
            Header = "Server name",
            PlaceholderText = "my-server",
            Text = existing?.Name ?? string.Empty,
            IsEnabled = existing is null,
            MaxLength = 64,
        };
        var transportBox = new ComboBox { Header = "Connection type", HorizontalAlignment = HorizontalAlignment.Stretch };
        transportBox.Items.Add(new ComboBoxItem { Content = "Streamable HTTP" });
        transportBox.Items.Add(new ComboBoxItem { Content = "STDIO command" });
        transportBox.SelectedIndex = existing?.Transport == McpTransportType.Stdio ? 1 : 0;
        var urlBox = new TextBox
        {
            Header = "Server URL",
            PlaceholderText = "https://example.com/mcp",
            Text = existing?.Url ?? string.Empty,
        };
        var tokenEnvironmentBox = new TextBox
        {
            Header = "Bearer token environment variable (optional)",
            PlaceholderText = "MY_MCP_TOKEN",
            Text = existing?.BearerTokenEnvironmentVariable ?? string.Empty,
        };
        var commandBox = new TextBox
        {
            Header = "Command",
            PlaceholderText = "npx",
            Text = existing?.Command ?? string.Empty,
        };
        var argumentsBox = new TextBox
        {
            Header = "Arguments (one per line)",
            PlaceholderText = "-y\n@company/server",
            Text = existing is null ? string.Empty : string.Join(Environment.NewLine, existing.Arguments),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 110,
        };
        var httpPanel = new StackPanel { Spacing = 12 };
        httpPanel.Children.Add(urlBox);
        httpPanel.Children.Add(tokenEnvironmentBox);
        var stdioPanel = new StackPanel { Spacing = 12 };
        stdioPanel.Children.Add(commandBox);
        stdioPanel.Children.Add(argumentsBox);
        var errorText = CreateDialogErrorText();
        var panel = new StackPanel { Spacing = 12, MinWidth = 320, MaxWidth = 560 };
        panel.Children.Add(nameBox);
        panel.Children.Add(transportBox);
        panel.Children.Add(httpPanel);
        panel.Children.Add(stdioPanel);
        panel.Children.Add(errorText);

        void UpdateTransportFields()
        {
            var isHttp = transportBox.SelectedIndex == 0;
            httpPanel.Visibility = isHttp ? Visibility.Visible : Visibility.Collapsed;
            stdioPanel.Visibility = isHttp ? Visibility.Collapsed : Visibility.Visible;
        }
        transportBox.SelectionChanged += (_, _) => UpdateTransportFields();
        UpdateTransportFields();

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = existing is null ? "Add MCP server" : $"Edit {existing.Name}",
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 560,
            },
            PrimaryButtonText = existing is null ? "Add server" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        McpServerDraft? draft = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                var transport = transportBox.SelectedIndex == 0
                    ? McpTransportType.StreamableHttp
                    : McpTransportType.Stdio;
                draft = new McpServerDraft(
                    nameBox.Text.Trim(),
                    transport,
                    urlBox.Text.Trim(),
                    commandBox.Text.Trim(),
                    argumentsBox.Text.Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    tokenEnvironmentBox.Text.Trim());
                CodexCliIntegrationService.Validate(draft);
                errorText.Visibility = Visibility.Collapsed;
            }
            catch (Exception error)
            {
                args.Cancel = true;
                errorText.Text = error.Message;
                errorText.Visibility = Visibility.Visible;
            }
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? draft : null;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string actionLabel)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
            },
            PrimaryButtonText = actionLabel,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static TextBlock CreateDialogErrorText() => new()
    {
        Foreground = ResourceBrush("AppDangerBrush"),
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };

    private void SetBusy(bool isBusy, string? message = null)
    {
        BusyOverlay.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = message ?? "Refreshing integrations…";
        RefreshButton.IsEnabled = !isBusy;
        OpenConfigButton.IsEnabled = !isBusy;
        NewSkillButton.IsEnabled = !isBusy;
        BrowsePluginsButton.IsEnabled = !isBusy;
        ReloadMcpButton.IsEnabled = !isBusy;
        AddMcpButton.IsEnabled = !isBusy;
    }

    private static string CountLabel(int visible, int total, string noun)
    {
        var plural = total == 1 ? noun : $"{noun}s";
        return visible == total ? $"{total:N0} {plural}" : $"Showing {visible:N0} of {total:N0} {plural}";
    }

    private static bool Contains(string? value, string query) =>
        string.IsNullOrWhiteSpace(query) ||
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static Brush ResourceBrush(string key) =>
        (Brush)Application.Current.Resources[key];

    private static void OpenCodexConfig()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        Directory.CreateDirectory(codexHome);
        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        startInfo.ArgumentList.Add(Path.GetFullPath(codexHome));
        Process.Start(startInfo);
    }

    private void ShowError(string title, string message) =>
        ShowStatus(title, message, InfoBarSeverity.Error);

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;
    }

    public sealed class SkillIntegrationRow
    {
        public SkillIntegrationRow(SkillIntegration skill, bool canEdit)
        {
            Name = skill.Name;
            DisplayName = skill.DisplayName;
            Description = skill.Description;
            Path = skill.Path;
            Scope = skill.Scope;
            Enabled = skill.Enabled;
            CanEdit = canEdit;
        }

        public string Name { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public string Path { get; }
        public string Scope { get; }
        public bool Enabled { get; }
        public bool CanEdit { get; }
        public string StateText => Enabled ? "Enabled" : "Disabled";
        public Brush StateBrush => ResourceBrush(Enabled ? "AppGoodBrush" : "AppMutedBrush");
        public string Metadata => $"{Scope}  •  {Path}";
        public string ToggleLabel => Enabled ? "Disable" : "Enable";
        public string ToggleAutomationName => $"{ToggleLabel} {DisplayName}";
        public string EditAutomationName => $"Edit {DisplayName}";
        public string RemoveAutomationName => $"Remove {DisplayName}";
        public Visibility EditVisibility => CanEdit ? Visibility.Visible : Visibility.Collapsed;
    }

    public sealed class PluginIntegrationRow
    {
        public PluginIntegrationRow(PluginIntegration plugin)
        {
            Id = plugin.Id;
            Name = plugin.Name;
            DisplayName = plugin.DisplayName;
            Description = string.IsNullOrWhiteSpace(plugin.Description)
                ? "No description provided by this marketplace."
                : plugin.Description;
            MarketplaceName = plugin.MarketplaceName;
            MarketplaceDisplayName = plugin.MarketplaceDisplayName;
            MarketplacePath = plugin.MarketplacePath;
            Version = plugin.Version;
            Installed = plugin.Installed;
            Enabled = plugin.Enabled;
            CanManage = plugin.Installed ||
                        plugin.InstallPolicy != "NOT_AVAILABLE" &&
                        plugin.Availability != "DISABLED_BY_ADMIN";
        }

        public string Id { get; }
        public string Name { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public string MarketplaceName { get; }
        public string MarketplaceDisplayName { get; }
        public string? MarketplacePath { get; }
        public string? Version { get; }
        public bool Installed { get; }
        public bool Enabled { get; }
        public bool CanManage { get; }
        public string StateText => Installed ? Enabled ? "Installed" : "Disabled" : "Available";
        public Brush StateBrush => ResourceBrush(Installed && Enabled ? "AppGoodBrush" : "AppMutedBrush");
        public string Metadata => string.IsNullOrWhiteSpace(Version)
            ? MarketplaceDisplayName
            : $"{MarketplaceDisplayName}  •  {Version}";
        public string ActionLabel => Installed ? "Uninstall" : "Install";
        public string ActionAutomationName => $"{ActionLabel} {DisplayName}";
    }

    public sealed class McpIntegrationRow
    {
        public McpIntegrationRow(McpServerIntegration server)
        {
            Name = server.Name;
            Enabled = server.Enabled;
            Transport = server.Transport;
            Url = server.Url;
            Command = server.Command;
            Arguments = server.Arguments;
            BearerTokenEnvironmentVariable = server.BearerTokenEnvironmentVariable;
            AuthStatus = server.AuthStatus;
            HasAdvancedSettings = server.HasAdvancedSettings;
            Endpoint = server.Endpoint;
        }

        public string Name { get; }
        public bool Enabled { get; }
        public McpTransportType Transport { get; }
        public string? Url { get; }
        public string? Command { get; }
        public IReadOnlyList<string> Arguments { get; }
        public string? BearerTokenEnvironmentVariable { get; }
        public string AuthStatus { get; }
        public bool HasAdvancedSettings { get; }
        public bool CanEdit => !HasAdvancedSettings;
        public string Endpoint { get; }
        public string TransportLabel => Transport == McpTransportType.StreamableHttp ? "HTTP" : "STDIO";
        public string StateText => Enabled ? "Enabled" : "Disabled";
        public Brush StateBrush => ResourceBrush(Enabled ? "AppGoodBrush" : "AppMutedBrush");
        public string Metadata => HasAdvancedSettings
            ? $"{TransportLabel}  •  {AuthStatus}  •  Advanced settings protected"
            : $"{TransportLabel}  •  {AuthStatus}";
        public string EditLabel => HasAdvancedSettings ? "Advanced" : "Edit";
        public string EditAutomationName => HasAdvancedSettings
            ? $"Advanced settings for {Name} are protected"
            : $"Edit {Name}";
        public string RemoveAutomationName => $"Remove {Name}";
    }
}
