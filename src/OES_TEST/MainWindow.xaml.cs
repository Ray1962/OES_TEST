using System.Windows;

namespace OesTest;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.Dispose();
        };
    }
}
