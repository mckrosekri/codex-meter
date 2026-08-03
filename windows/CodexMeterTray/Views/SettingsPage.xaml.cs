using CodexDecision.Core.Conversations;
using CodexDecision.Core.Projects;
using CodexDecision.Core.Routing;
using CodexDecision.Core.SpecialSkills;
using CodexMeterTray.Services;

namespace CodexMeterTray.Views;

public partial class SettingsPage : Page
{
    private readonly AppServices services = ((App)Application.Current).Services;
    private bool isLoaded;
    private bool isRendering;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
        Unloaded += SettingsPage_Unloaded;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (isLoaded)
        {
            return;
        }

        isLoaded = true;
        services.Changed += Services_Changed;
        Render();
    }

    private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        isLoaded = false;
        services.Changed -= Services_Changed;
    }

    private void Services_Changed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(Render);
    }

    private void Render()
    {
        isRendering = true;
        try
        {
            SecureContinuityToggle.IsOn = services.Workspace.State.SecureContinuityMode is
                SecureContinuityMode.DpapiCurrentUser;
            RoutingPolicyCombo.SelectedIndex = services.Workspace.State.RoutingPolicy switch
            {
                RoutingPolicy.Eco => 0,
                RoutingPolicy.Quality => 2,
                _ => 1,
            };
            NotificationPreferenceCombo.SelectedIndex = services.Workspace.State.NotificationPreference switch
            {
                NotificationPreference.Never => 0,
                NotificationPreference.Always => 2,
                _ => 1,
            };

            var promptMasterEnabled = services.Workspace.IsSpecialSkillEnabled(SpecialSkillId.PromptMasterCodex);
            PromptMasterToggle.IsOn = promptMasterEnabled;
            PromptMasterStatusText.Text = promptMasterEnabled
                ? "Active for new sends and steering. Queued messages keep the setting captured when queued."
                : "Turns rough wording into a precise principal-engineering brief.";
        }
        finally
        {
            isRendering = false;
        }
    }

    private async void SecureContinuityToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (isRendering || !isLoaded)
        {
            return;
        }

        SecureContinuityToggle.IsEnabled = false;
        try
        {
            await services.SetSecureContinuityAsync(SecureContinuityToggle.IsOn
                ? SecureContinuityMode.DpapiCurrentUser
                : SecureContinuityMode.Disabled);
        }
        catch (Exception error)
        {
            Render();
            ShowError("Encrypted continuity could not be updated", error.Message);
        }
        finally
        {
            SecureContinuityToggle.IsEnabled = true;
        }
    }

    private async void RoutingPolicyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRendering || !isLoaded || RoutingPolicyCombo.SelectedIndex < 0)
        {
            return;
        }

        var policy = RoutingPolicyCombo.SelectedIndex switch
        {
            0 => RoutingPolicy.Eco,
            2 => RoutingPolicy.Quality,
            _ => RoutingPolicy.Balanced,
        };
        await services.Workspace.SetRoutingPolicyAsync(policy);
        services.NotifyWorkspaceChanged();
    }

    private async void NotificationPreferenceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRendering || !isLoaded || NotificationPreferenceCombo.SelectedIndex < 0)
        {
            return;
        }

        var preference = NotificationPreferenceCombo.SelectedIndex switch
        {
            0 => NotificationPreference.Never,
            2 => NotificationPreference.Always,
            _ => NotificationPreference.BackgroundOnly,
        };
        await services.Workspace.SetNotificationPreferenceAsync(preference);
        services.NotifyWorkspaceChanged();
    }

    private async void PromptMasterToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (isRendering || !isLoaded)
        {
            return;
        }

        PromptMasterToggle.IsEnabled = false;
        try
        {
            if (PromptMasterToggle.IsOn)
            {
                _ = AppServices.ResolveSpecialSkills([SpecialSkillId.PromptMasterCodex]);
            }

            await services.Workspace.SetSpecialSkillEnabledAsync(
                SpecialSkillId.PromptMasterCodex,
                PromptMasterToggle.IsOn);
            services.NotifyWorkspaceChanged();
        }
        catch (Exception error)
        {
            Render();
            ShowError("Special skill could not be updated", error.Message);
        }
        finally
        {
            PromptMasterToggle.IsEnabled = true;
        }
    }

    private void ShowError(string title, string message)
    {
        SettingsInfoBar.Title = title;
        SettingsInfoBar.Message = message;
        SettingsInfoBar.IsOpen = true;
    }
}
