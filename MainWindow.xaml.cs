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
    /// 主窗口：负责 UI 交互，并把用户的点击操作转交给 SecsEquipmentService。
    /// 
    /// 设计原则：
    /// 1. 窗口本身不直接操作 SecsGem/HsmsConnection，所有 SECS 相关逻辑都封装在 SecsEquipmentService 里。
    /// 2. 后台线程产生的日志通过 SecsEquipmentService.LogReceived 事件“快递”过来，
    ///    这里再用 Dispatcher 切回 UI 线程更新 ObservableCollection。
    /// 3. ObservableCollection 只能在 UI 线程修改，否则会抛跨线程访问异常。
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// 日志集合，直接绑定到 XAML 里的 ListView。
        /// 用 ObservableCollection 是因为它实现了 INotifyCollectionChanged，
        /// 新增/删除元素时 UI 会自动刷新，不需要手动调用 Refresh。
        /// </summary>
        public ObservableCollection<SecsLogItem> Messages { get; } = new();

        /// <summary>
        /// 设备端服务实例。为 null 表示当前没有启动监听。
        /// 生命周期与“启动监听/停止”按钮绑定：启动时 new，停止时 DisposeAsync 并置 null。
        /// </summary>
        private SecsEquipmentService? _equipment;

        public MainWindow()
        {
            InitializeComponent();

            // 把 ObservableCollection 绑定到 ListView。
            // 也可以写在 XAML 里（ItemsSource="{Binding Messages}" + DataContext），
            // 这里用代码绑定是为了让示例更直观、不依赖 DataContext 的设置。
            lstMessages.ItemsSource = Messages;
        }

        /// <summary>
        /// “启动监听”按钮：创建设备端服务并开始被动监听 Host 连接。
        /// </summary>
        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 每次启动都创建一个新的服务实例，避免上一次停止后的残留状态。
                _equipment = new SecsEquipmentService();

                // 把后台的“日志事件”和 UI 的“添加日志方法”绑定起来。
                // 
                // 关键点：LogReceived 可能在线程池线程上触发（例如接收循环、连接状态回调），
                // 而 AddLog 会修改 ObservableCollection，必须在 UI 线程执行。
                // 所以这里用 Dispatcher.Invoke 切回 UI 线程。
                // 
                // 注意：Dispatcher.Invoke 是同步阻塞的——后台线程会等 UI 线程处理完才继续。
                // 如果日志非常密集，建议改成 Dispatcher.BeginInvoke（异步排队，不阻塞后台）。
                // 当前设备端消息量不大，用 Invoke 可以保证日志顺序严格一致。
                _equipment.LogReceived += item => Dispatcher.Invoke(() => AddLog(item));

                // 启动监听。参数来自界面上的三个输入框：
                // ip：本机要绑定的监听地址（被动模式下是“本机 IP”）
                // port：HSMS 监听端口
                // deviceId：SECS 设备 ID，Host 发来的报文里会带这个 ID，必须匹配
                _equipment.Start(
                    txtIp.Text.Trim(),
                    int.Parse(txtPort.Text),
                    ushort.Parse(txtDeviceId.Text));

                // 更新 UI 状态：监听中，禁用启动按钮，启用停止按钮。
                txtState.Text = "监听中";
                txtState.Foreground = System.Windows.Media.Brushes.Green;
                btnStart.IsEnabled = false;
                btnStop.IsEnabled = true;
            }
            catch (Exception ex)
            {
                // 启动失败（例如端口被占用、IP 格式错误、DeviceId 解析失败）时，
                // 把异常信息作为一条 Error 日志显示出来，而不是让程序崩溃。
                AddLog(new SecsLogItem { Direction = LogDirection.Error, Content = ex.Message });
            }
        }

        /// <summary>
        /// “停止”按钮：释放设备端服务，断开连接并停止接收循环。
        /// 
        /// 注意这里是 async void——事件处理器允许 async void，但内部必须自己 try/catch，
        /// 否则 DisposeAsync 抛出的异常会直接冒泡到 DispatcherUnhandledException 导致程序崩溃。
        /// </summary>
        private async void btnStop_Click(object sender, RoutedEventArgs e)
        {
            if (_equipment is not null)
            {
                try
                {
                    // DisposeAsync 会：取消接收循环的 CancellationToken、等待接收 Task 退出、
                    // 释放 HsmsConnection（关闭监听 socket 和已建立的连接）。
                    await _equipment.DisposeAsync();
                }
                catch (Exception ex)
                {
                    // 释放过程中即使出错也不应让 UI 崩溃，记录日志即可。
                    AddLog(new SecsLogItem { Direction = LogDirection.Error, Content = ex.Message });
                }
                finally
                {
                    _equipment = null;
                }
            }

            // 无论释放是否成功，UI 都回到“已停止”状态，允许重新启动。
            txtState.Text = "已停止";
            txtState.Foreground = System.Windows.Media.Brushes.Gray;
            btnStart.IsEnabled = true;
            btnStop.IsEnabled = false;
        }

        /// <summary>
        /// “主动上报 S6F11”按钮：模拟设备端向 Host 发送事件报告。
        /// 
        /// S6F11 是 GEM 里设备主动上报事件的标准消息，Host 收到后应回 S6F12。
        /// 这里为了演示，CEID/RPTID/变量值都从界面输入框读取。
        /// </summary>
        private async void btnReport_Click(object sender, RoutedEventArgs e)
        {
            if (_equipment is null)
            {
                AddLog(new SecsLogItem { Direction = LogDirection.Error, Content = "请先启动监听" });
                return;
            }

            try
            {
                // 调用服务层封装的 ReportEventAsync。
                // 这里只传了一个 A 类型（ASCII）的变量值，实际设备可能有多个变量，
                // 届时可以传多个 Item（params Item[] data）。
                await _equipment.ReportEventAsync(
                    ceid: ushort.Parse(txtCeid.Text),
                    rptid: uint.Parse(txtRptid.Text),
                    Secs4Net.Item.A(txtValue.Text));
            }
            catch (Exception ex)
            {
                // 解析输入失败（例如 CEID 不是数字）或发送失败时，记录错误日志。
                AddLog(new SecsLogItem { Direction = LogDirection.Error, Content = ex.Message });
            }
        }

        /// <summary>
        /// 所有日志统一从这里进入 UI 集合。
        /// 
        /// 这个方法必须只在 UI 线程被调用（由 Dispatcher.Invoke/BeginInvoke 保证），
        /// 因为 ObservableCollection 不是线程安全的。
        /// </summary>
        private void AddLog(SecsLogItem item)
        {
            Messages.Add(item);

            // 如果勾选了“自动滚动”，把列表滚到最后一条，方便观察最新消息。
            // Messages[^1] 是 C# 8 的索引语法，等价于 Messages[Messages.Count - 1]。
            if (chkAutoScroll.IsChecked == true && Messages.Count > 0)
                lstMessages.ScrollIntoView(Messages[^1]);
        }
    }
}