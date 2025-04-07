using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using bcnvision.Communications;
using bcnvision.Data;
using bcnvision.Tools;


namespace bcnvision
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public BcnUdp udp;
        #region Constructor
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="Udp">Dispostivo de comunicacion</param>
        /// 

        public AdvancedScript _script { get; private set; }
        private bool _isRunning = false;
        private bool _stopRequested = false;
        //private AdvancedScript _script;
        private Dictionary<string, CheckBox> vsSwitches = new Dictionary<string, CheckBox>();
        private Dictionary<string, TextBox> vsTextBoxes = new Dictionary<string, TextBox>();
        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            //_script = new AdvancedScript();
            //// Crear una instancia de AdvancedScript y asignarla a AdvancedScriptInstance
            _script = new AdvancedScript();
            CrearPanelesVisionSystems();
        }

        private void CrearPanelesVisionSystems()
        {
            foreach (var vs in BcnConfigFile.Configuration.VisionSystems)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5) };

                var label = new Label { Content = vs.Name, Width = 100, VerticalAlignment = VerticalAlignment.Center };
                var textbox = new TextBox { Width = 200, Margin = new Thickness(5), IsEnabled = false };
                var toggle = new CheckBox { Content = "Manual", Width = 80, VerticalAlignment = VerticalAlignment.Center };
                toggle.Checked += (s, e) => SwitchToManual(vs.Name);
                toggle.Unchecked += (s, e) => SwitchToAutomatic(vs.Name);

                vsTextBoxes[vs.Name] = textbox;
                vsSwitches[vs.Name] = toggle;

                panel.Children.Add(label);
                panel.Children.Add(textbox);
                panel.Children.Add(toggle);
                ManualLotPanel.Children.Add(panel);
            }
        }
        private void EnableManualLotCheck_Checked(object sender, RoutedEventArgs e)
        {
            ManualLotPanel.Visibility = Visibility.Visible;
        }

        private void EnableManualLotCheck_Unchecked(object sender, RoutedEventArgs e)
        {
            ManualLotPanel.Visibility = Visibility.Collapsed;
        }

        private void SwitchToManual(string vsName)
        {
            var tb = vsTextBoxes[vsName];
            var switchBox = vsSwitches[vsName];

            string lote = tb.Text.Trim();
            if (!Regex.IsMatch(lote, @"^[A-Za-z0-9\-]{27}$"))
            {
                MessageBox.Show($"El lote ingresado en '{vsName}' no es válido. Debe tener exactamente 27 caracteres alfanuméricos o guiones.", "Error de formato", MessageBoxButton.OK, MessageBoxImage.Warning);
                switchBox.IsChecked = false; // volver a automático
                return;
            }

            tb.IsEnabled = true;
            _script.ActivarLoteManual(vsName, lote);
        }

        private void SwitchToAutomatic(string vsName)
        {
            var tb = vsTextBoxes[vsName];
            tb.Text = string.Empty;
            tb.IsEnabled = false;
            _script.DesactivarLoteManual(vsName);
        }

        private void SendTriggerButton_Click(object sender, RoutedEventArgs e)
        {
            Task.Run(() =>
            {
                try
                {
                    while (true)
                    {

                        for (int i = 0; i < 5; i++)
                        {
                            // Simula la acción de escribir en UDP
                            udp.Write("TRIGGER_" + i.ToString("00"), "1");
                            //Thread.Sleep(10); // Simula un pequeño retraso
                        }

                        // Mantiene el evento "clavado" durante el tiempo definido
                        Thread.Sleep(1000);
                    }


                }
                catch (Exception ex)
                {
                }
            });
        }
    }

}
#endregion