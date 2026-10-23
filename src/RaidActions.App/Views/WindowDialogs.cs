using System.Windows.Controls;
using Microsoft.Win32;
using RaidActions.App.ViewModels;

namespace RaidActions.App.Views;

/// <summary>The real dialogs: a file picker and the dark confirmation box listing the steps and donors.</summary>
public sealed class WindowDialogs : IDialogs
{
    public string? OpenPlan(string initialFolder)
    {
        var d = new OpenFileDialog { Title = "Open a plan", Filter = "Plan files (*.json)|*.json", InitialDirectory = initialFolder };
        return d.ShowDialog() == true ? d.FileName : null;
    }

    public async Task<bool> ConfirmAsync(string title, string body, string startText)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 480,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock { Text = body, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12 },
            },
            PrimaryButtonText = startText,
            CloseButtonText = "Cancel",
            MinWidth = 720,
        };
        return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }
}
