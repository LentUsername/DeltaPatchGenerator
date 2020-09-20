using DeltaPatchGenerator;
using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace DeltaPatchGeneratorWindowsGUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        readonly Config config;
        public MainWindow()
        {
            InitializeComponent();
            Trace.Listeners.Add(new TextBoxTraceListener(logTextBox, SynchronizationContext.Current));
            config = Program.LoadConfig();
            outputTextBox.Text = config.OutputPath;
            sourceTextBox.Text = config.SourcePath;
            targetTextBox.Text = config.TargetPath;
        }

        static void SetTextToSelection(TextBox textBox)
        {
            using (var dialog = new CommonOpenFileDialog { IsFolderPicker = true })
            {
                if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
                    textBox.Text = dialog.FileName;
            }
        }

        void SourceBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            SetTextToSelection(sourceTextBox);
        }


        void TargetBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            SetTextToSelection(targetTextBox);
        }

        void OutputBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            SetTextToSelection(outputTextBox);
        }

        async void GeneratePatchButton_Click(object sender, RoutedEventArgs e)
        {
            ((Button)sender).IsEnabled = false;
            Program.SaveConfig(config);
            await Task.Run(() => Program.DoPatch(config));
            ((Button)sender).IsEnabled = true;
        }

        void SourceTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            config.SourcePath = ((TextBox)sender).Text;
        }

        void TargetTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            config.TargetPath = ((TextBox)sender).Text;
        }

        void OutputTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            config.OutputPath = ((TextBox)sender).Text;
        }

        void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            Program.SaveConfig(config);
        }

        void MenuHelpAbout_Click(object sender, RoutedEventArgs e)
        {
            new AboutWindow().ShowDialog();
        }
    }
}
