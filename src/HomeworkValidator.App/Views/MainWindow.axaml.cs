using Avalonia.Controls;
using HomeworkValidator.App.ViewModels;

namespace HomeworkValidator.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, _) => (DataContext as MainViewModel)?.SaveAll();
    }
}
