using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WpfApp1
{
    public partial class MainWindow : Window
    {
        private bool isPrinting = false;
        private bool isPaused = false;
        private bool isMaximized = false;
        private DispatcherTimer timer;
        private int elapsedSeconds = 0;

        public MainWindow()
        {
            InitializeComponent();
            InitializeTimer();
        }

        #region 窗口控制

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
                BtnMaximize.Content = "[]";
                isMaximized = false;
            }
            else
            {
                this.WindowState = WindowState.Maximized;
                BtnMaximize.Content = "[ ]";
                isMaximized = true;
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            ShowExitConfirmation();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                if (this.WindowState == WindowState.Maximized)
                {
                    var point = e.GetPosition(this);
                    this.WindowState = WindowState.Normal;
                    this.Left = point.X - this.Width / 2;
                    this.Top = point.Y - 20;
                }
                this.DragMove();
            }
        }

        #endregion

        #region 快捷操作按钮

        private void BtnStartPrint_Click(object sender, RoutedEventArgs e)
        {
            if (isPrinting && !isPaused)
            {
                MessageBox.Show("正在打印中...", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            isPrinting = true;
            isPaused = false;
            timer.Start();
            UpdatePrintButtonState();

            AddLogEntry("转移打印开始执行", "#6366F1");
        }

        private void BtnPausePrint_Click(object sender, RoutedEventArgs e)
        {
            if (!isPrinting)
            {
                MessageBox.Show("请先开始打印", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            isPaused = true;
            timer.Stop();
            UpdatePrintButtonState();

            AddLogEntry("打印任务已暂停", "#F59E0B");
        }

        private void BtnEmergencyStop_Click(object sender, RoutedEventArgs e)
        {
            isPrinting = false;
            isPaused = false;
            timer.Stop();
            UpdatePrintButtonState();

            AddLogEntry("紧急停止触发", "#EF4444");

            MessageBox.Show(
                "紧急停止已触发!\n\n请立即检查设备状态。",
                "紧急停止",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private void BtnDebug_Click(object sender, RoutedEventArgs e)
        {
            AddLogEntry("进入停机调试模式", "#9CA3AF");
            MessageBox.Show("已进入调试模式", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogPanel.Children.Clear();
            AddLogEntry("日志已清空", "#9CA3AF");
        }

        private void BtnCancelExit_Click(object sender, RoutedEventArgs e)
        {
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

        #endregion

        #region 退出系统

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            if (isPrinting)
            {
                var result = MessageBox.Show(
                    "警告:打印任务正在进行中!\n\n确定要强制退出吗?",
                    "退出确认",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    BtnEmergencyStop_Click(sender, e);
                    Application.Current.Shutdown();
                }
            }
            else
            {
                var result = MessageBox.Show(
                    "确定要退出控制系统吗?",
                    "退出确认",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    timer.Stop();
                    Application.Current.Shutdown();
                }
            }
        }

        #endregion

        #region 辅助方法

        private void InitializeTimer()
        {
            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += Timer_Tick;
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (!isPaused)
            {
                elapsedSeconds++;
                UpdateRuntimeDisplay();

                if (isPrinting && ProgressBarPrint.Value < 100)
                {
                    ProgressBarPrint.Value += 0.1;
                    TxtProgress.Text = string.Format("{0:F0}%", ProgressBarPrint.Value);

                    int currentBatch = int.Parse(TxtCompletedBatch.Text);
                    if (currentBatch < 200)
                    {
                        TxtCompletedBatch.Text = (currentBatch + 1).ToString();
                    }
                }
            }
        }

        private void UpdateRuntimeDisplay()
        {
            int hours = elapsedSeconds / 3600;
            int minutes = (elapsedSeconds % 3600) / 60;
            int seconds = elapsedSeconds % 60;
            TxtRunTime.Text = string.Format("{0:D2}:{1:D2}:{2:D2}", hours, minutes, seconds);
        }

        private void UpdatePrintButtonState()
        {
            if (isPrinting && !isPaused)
            {
                BtnStartPrint.IsEnabled = false;
                BtnPausePrint.Content = "暂停打印";
                BtnPausePrint.IsEnabled = true;
            }
            else if (isPrinting && isPaused)
            {
                BtnStartPrint.IsEnabled = false;
                BtnPausePrint.Content = "继续打印";
                BtnPausePrint.IsEnabled = true;
            }
            else
            {
                BtnStartPrint.IsEnabled = true;
                BtnPausePrint.Content = "暂停打印";
                BtnPausePrint.IsEnabled = false;
            }
        }

        private void AddLogEntry(string message, string color)
        {
            TextBlock newEntry = new TextBlock
            {
                Text = string.Format("[{0}] {1}", DateTime.Now.ToString("HH:mm:ss"), message),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 6, 0, 0)
            };

            LogPanel.Children.Insert(0, newEntry);

            while (LogPanel.Children.Count > 50)
            {
                LogPanel.Children.RemoveAt(LogPanel.Children.Count - 1);
            }
        }

        #endregion
    }
}
