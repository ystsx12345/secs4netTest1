using Secs4Net;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace secs4netTest1
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>

    public partial class MainWindow : Window
    {
        public ObservableCollection<SecsLogItem> Messages { get; } = new();
        private SecsEquipmentService? _equipment;

        public MainWindow()
        {
            InitializeComponent();
            lstMessages.ItemsSource = Messages;
        }

        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _equipment = new SecsEquipmentService();
                // ⬇️ 这里把后台的快递员和界面的接收函数绑定起来
                _equipment.LogReceived += item => Dispatcher.Invoke(() => AddLog(item));

                _equipment.Start(
                    txtIp.Text.Trim(),
                    int.Parse(txtPort.Text),
                    ushort.Parse(txtDeviceId.Text));

                txtState.Text = "监听中";
                txtState.Foreground = System.Windows.Media.Brushes.Green;
                btnStart.IsEnabled = false;
                btnStop.IsEnabled = true;
            }
            catch (Exception ex)
            {
                AddLog(new SecsLogItem { Direction = LogDirection.Error, Content = ex.Message });
            }
        }

        private async void btnStop_Click(object sender, RoutedEventArgs e)
        {
            if (_equipment is not null)
            {
                await _equipment.DisposeAsync();
                _equipment = null;
            }
            txtState.Text = "已停止";
            txtState.Foreground = System.Windows.Media.Brushes.Gray;
            btnStart.IsEnabled = true;
            btnStop.IsEnabled = false;
        }

        private async void btnReport_Click(object sender, RoutedEventArgs e)
        {
            if (_equipment is null)
            {
                AddLog(new SecsLogItem { Direction = LogDirection.Error, Content = "请先启动监听" });
                return;
            }

            try
            {
                await _equipment.ReportEventAsync(
                    ceid: ushort.Parse(txtCeid.Text),
                    rptid: uint.Parse(txtRptid.Text),
                    Secs4Net.Item.A(txtValue.Text));
            }
            catch (Exception ex)
            {
                AddLog(new SecsLogItem { Direction = LogDirection.Error, Content = ex.Message });
            }
        }


        // 所有日志统一从这里进入，保证在 UI 线程
        private void AddLog(SecsLogItem item)
        {
            Messages.Add(item);
            if (chkAutoScroll.IsChecked == true && Messages.Count > 0)
                lstMessages.ScrollIntoView(Messages[^1]);
        }
    }

}