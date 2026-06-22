using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace WpfApp1
{
    public partial class MainWindow : Window
    {
        // 日志集合
        private ObservableCollection<TextBlock> _logItems = new ObservableCollection<TextBlock>();
        private DispatcherTimer _timer;
        private bool _isPrinting = false;
        private bool _isPaused = false;
        private int _currentBatch = 128;
        private int _totalBatch = 200;
        private int _progress = 65;
        private TimeSpan _runTime = new TimeSpan(3, 45, 22);

        public MainWindow()
        {
            InitializeComponent();

            // 启动计时器
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        // 计时器事件 - 更新运行时长
        private void Timer_Tick(object? sender, EventArgs e)
        {
            // 更新运行时长
            _runTime = _runTime.Add(TimeSpan.FromSeconds(1));
            TxtRunTime.Text = _runTime.ToString(@"hh\:mm\:ss");

            // 如果正在打印，更新进度
            if (_isPrinting && !_isPaused && _progress < 100)
            {
                _progress++;
                ProgressBarPrint.Value = _progress;
                TxtProgress.Text = _progress + "%";

                // 每完成一次批次
                if (_progress >= 100)
                {
                    _currentBatch++;
                    TxtCompletedBatch.Text = _currentBatch + " / " + _totalBatch;
                    _progress = 0;
                    ProgressBarPrint.Value = 0;
                    TxtProgress.Text = "0%";
                    AddLog("批次打印完成，进入下一批次");
                }
            }
        }

        // 添加日志
        private void AddLog(string message)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            var logEntry = new TextBlock
            {
                Text = $"[{time}] {message}",
                Foreground = GetLogBrush(message),
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0)
            };
            LogPanel.Children.Add(logEntry);
        }

        // 根据日志内容选择颜色
        private System.Windows.Media.Brush GetLogBrush(string message)
        {
            if (message.Contains("完成") || message.Contains("通过") || message.Contains("成功"))
                return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)); // 绿色
            else if (message.Contains("警告") || message.Contains("注意"))
                return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11)); // 黄色
            else if (message.Contains("错误") || message.Contains("失败"))
                return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68)); // 红色
            else
                return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(99, 91, 255)); // 紫色
        }

        // 标题栏拖拽移动
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                // 双击最大化/还原
                BtnMaximize_Click(sender, e);
            }
            else
            {
                if (WindowState == WindowState.Maximized)
                {
                    WindowState = WindowState.Normal;
                }
                DragMove();
            }
        }

        // 最小化按钮
        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        // 最大化按钮
        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                BtnMaximize.Content = "☐";
            }
            else
            {
                WindowState = WindowState.Maximized;
                BtnMaximize.Content = "❐";
            }
        }

        // 关闭按钮
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (_isPrinting)
            {
                MessageBoxResult result = MessageBox.Show(
                    "系统正在打印中，确定要退出吗？",
                    "退出确认",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    _timer.Stop();
                    Application.Current.Shutdown();
                }
            }
            else
            {
                _timer.Stop();
                Application.Current.Shutdown();
            }
        }

        // 开始打印
        private void BtnStartPrint_Click(object sender, RoutedEventArgs e)
        {
            if (!_isPrinting)
            {
                _isPrinting = true;
                _isPaused = false;
                BtnStartPrint.Content = "打印中...";
                BtnStartPrint.IsEnabled = false;
                BtnPausePrint.Content = "暂停";
                BtnPausePrint.IsEnabled = true;
                TxtCurrentProcess.Text = "转移打印";
                AddLog("开始打印批次 " + (_currentBatch + 1));
            }
        }

        // 暂停打印
        private void BtnPausePrint_Click(object sender, RoutedEventArgs e)
        {
            if (!_isPrinting) return;

            if (_isPaused)
            {
                _isPaused = false;
                BtnPausePrint.Content = "暂停";
                AddLog("恢复打印");
            }
            else
            {
                _isPaused = true;
                BtnPausePrint.Content = "继续";
                AddLog("暂停打印");
            }
        }

        // 停止打印
        private void BtnStopPrint_Click(object sender, RoutedEventArgs e)
        {
            if (!_isPrinting) return;

            MessageBoxResult result = MessageBox.Show(
                "确定要停止当前打印任务吗？",
                "停止确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _isPrinting = false;
                _isPaused = false;
                _progress = 0;
                ProgressBarPrint.Value = 0;
                TxtProgress.Text = "0%";
                BtnStartPrint.Content = "开始打印批次";
                BtnStartPrint.IsEnabled = true;
                BtnPausePrint.Content = "暂停";
                BtnPausePrint.IsEnabled = false;
                TxtCurrentProcess.Text = "待机";
                AddLog("用户停止打印");
            }
        }

        // 停机调试
        private void BtnDebug_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "进入停机调试模式，请注意安全！",
                "调试模式",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            AddLog("进入停机调试模式");
        }

        // 清空日志
        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogPanel.Children.Clear();
            AddLog("日志已清空");
        }

        // 退出系统
        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            BtnClose_Click(sender, e);
        }

        // 取消退出
        private void BtnCancelExit_Click(object sender, RoutedEventArgs e)
        {
            // 切换到第一个标签页
            var tabControl = this.Content as Grid;
            if (tabControl != null)
            {
                foreach (var child in tabControl.Children)
                {
                    if (child is TabControl tc)
                    {
                        tc.SelectedIndex = 0;
                        break;
                    }
                }
            }
        }
    }
}
