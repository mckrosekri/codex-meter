namespace CodexMeterTray.Views;

public partial class WelcomePage : Page
{
    public WelcomePage()
    {
        InitializeComponent();
    }

    public event EventHandler? NewQuestionRequested;

    public event EventHandler? AddProjectRequested;

    private void NewQuestionButton_Click(object sender, RoutedEventArgs e)
    {
        NewQuestionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void AddProjectButton_Click(object sender, RoutedEventArgs e)
    {
        AddProjectRequested?.Invoke(this, EventArgs.Empty);
    }
}
