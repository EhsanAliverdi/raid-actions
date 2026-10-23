using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace RaidActions.App.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        // Fit the screen (small or high-DPI displays), and stack the panels when narrow.
        var work = SystemParameters.WorkArea;
        Width = Math.Min(Width, work.Width);
        Height = Math.Min(Height, work.Height);
        SizeChanged += (_, _) => Arrange(ActualWidth < 1000);
        Arrange(Width < 1000);
        // Keep the newest log line in view.
        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        };
    }

    private void Arrange(bool narrow)
    {
        StepsColumn.Width = new GridLength(3, GridUnitType.Star);
        GapColumn.Width = new GridLength(narrow ? 0 : 12);
        SideColumn.Width = narrow ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        SideRow.Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(Side, narrow ? 1 : 0);
        Grid.SetColumn(Side, narrow ? 0 : 2);
        Side.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
    }
}
