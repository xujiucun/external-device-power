using System.Windows;
using System.Windows.Input;

namespace DevicePower;

public partial class WakeTipWindow : Window
{
    public WakeTipWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}