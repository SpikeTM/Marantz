using System.Windows;
using MarantzDesktopControl.Models;

namespace MarantzDesktopControl;

public partial class ProfileSetupWindow : Window
{
    public ReceiverProfile? Result { get; private set; }

    public ProfileSetupWindow()
    {
        InitializeComponent();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        string name = ProfileNameTextBox.Text.Trim();
        string ip = IpAddressTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            ValidationText.Text = "Profile name is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ip))
        {
            ValidationText.Text = "Receiver IP is required.";
            return;
        }

        Result = new ReceiverProfile
        {
            Name = name,
            IpAddress = ip,
            MainZoneName = "MAIN ZONE",
            Zone2Name = "ZONE2"
        };

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
