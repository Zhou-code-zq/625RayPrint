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
        private DispatcherTimer timer;
        private int elapsedSeconds = 0;

        public MainWindow()
        {
            InitializeComponent();
            InitializeTimer();
        }

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
            }
            else
            {
                this.WindowState = WindowState.Maximized;
                BtnMaximize.Content = "[ ]";
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
                    double mouseX = e.GetPosition(this).X;
                    double mouseY = e.GetPosition(this).Y;
                    this.WindowState = WindowState.Normal;
                    this.Left = mouseX - (this.Width / 2);
                    this.Top = mouseY - 20;
                }
                this.DragMove();
            }
        }

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
            Grid rootGrid = this.Content as Grid;
            if (rootGrid != null)
            {
                foreach (object child in rootGrid.Children)
                {
                    if (child is TabControl tc)
                    {
                        tc.SelectedIndex = 0;
                        break;
                    }
                }
            }
        }

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            if (isPrinting)
            {
                MessageBoxResult result = MessageBox.Show(
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
                MessageBoxResult result = MessageBox.Show(
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
            TextBlock newEntry = new TextBlock();
            newEntry.Text = string.Format("[{0}] {1}", DateTime.Now.ToString("HH:mm:ss"), message);
            newEntry.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            newEntry.FontSize = 11;
            newEntry.FontFamily = new FontFamily("Consolas");
            newEntry.Margin = new Thickness(0, 6, 0, 0);

            LogPanel.Children.Insert(0, newEntry);

            while (LogPanel.Children.Count > 50)
            {
                LogPanel.Children.RemoveAt(LogPanel.Children.Count - 1);
            }
        }

        private void ShowExitConfirmation()
        {
            BtnExit_Click(this, new RoutedEventArgs());
        }
    }
}
