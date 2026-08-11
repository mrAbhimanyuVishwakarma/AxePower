using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AxePower.App.Windows;

public partial class GameScannerWindow : Window
{
    public ObservableCollection<ScannedGameItem> Games { get; } = new();

    public IEnumerable<string> SelectedGames => Games.Where(g => g.IsSelected).Select(g => g.Name);

    public GameScannerWindow(List<string> foundGames)
    {
        InitializeComponent();
        
        foreach (var game in foundGames)
        {
            Games.Add(new ScannedGameItem { Name = game, IsSelected = true });
        }
        
        GamesList.ItemsSource = Games;
    }

    private void AddSelected_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

public partial class ScannedGameItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isSelected;
}
