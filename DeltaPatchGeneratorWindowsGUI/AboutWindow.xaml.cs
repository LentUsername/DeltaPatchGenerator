using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Documents;

namespace DeltaPatchGeneratorWindowsGUI
{
    /// <summary>
    /// Interaction logic for AboutWindow.xaml
    /// </summary>
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            licenseTextBox.Text = Encoding.UTF8.GetString(Properties.Resources.LICENSE);
        }

        void Hyperlink_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(((Hyperlink)sender).NavigateUri.ToString());
        }
    }
}
