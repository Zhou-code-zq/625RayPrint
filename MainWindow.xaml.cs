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
        // 状态标志
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

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // 窗口加载完成后聚焦，以便接收键盘事件
            this.Focus();
        }

        #region 窗口控制

        // 最小化
        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        // 最大化/还原
        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
                BtnMaximize.Content = "☐";
                isMaximized = false;
            }
            else
            {
                this.WindowState = WindowState.Maximized;
                BtnMaximize.Content = "❐";
                isMaximized = true;
            }
        }

        // 关闭
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            ShowExitConfirmation();
        }

        // 无边框窗口拖拽
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            // 如果点击的是窗口内容区域（非按钮等控件），允许拖拽
            if (e.OriginalSource is FrameworkElement element)
            {
                // 检查是否点击在可交互控件上
                if (element.Name == "BtnMinimize" || element.Name == "BtnMaximize" || 
                    element.Name == "BtnClose" || element.Name == "LogPanel")
                    return;
            }

            if (e.LeftButton == MouseButtonState.Pressed)
            {
                // 如果窗口最大化，则还原后再拖拽
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

        // 双击标题栏最大化/还原
        protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            BtnMaximize_Click(sender: null, e: null);
        }

        #endregion

        #region 快捷键

        // 键盘事件处理
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+P: 开始打印
            if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
            {
                BtnStartPrint_Click(sender, e);
                e.Handled = true;
            }
            // Space: 暂停/继续
            else if (e.Key == Key.Space && !isPaused)
            {
                BtnPausePrint_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Space && isPaused)
            {
                ResumePrint();
                e.Handled = true;
            }
            // Esc: 紧急停止
            else if (e.Key == Key.Escape)
            {
                BtnEmergencyStop_Click(sender, e);
                e.Handled = true;
            }
            // Ctrl+D: 调试参数
            else if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
            {
                BtnDebug_Click(sender, e);
                e.Handled = true;
            }
            // F11: 全屏切换
            else if (e.Key == Key.F11)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
            // Alt+N: 最小化
            else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Alt)
            {
                BtnMinimize_Click(sender, e);
                e.Handled = true;
            }
            // Alt+M: 最大化
            else if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.Alt)
            {
                BtnMaximize_Click(sender, e);
                e.Handled = true;
            }
        }

        // 全屏切换
        private void ToggleFullScreen()
        {
            if (this.WindowState == WindowState.Maximized && this.WindowStyle == WindowStyle.None)
            {
                // 退出全屏
                this.WindowStyle = WindowStyle.None;
                this.WindowState = WindowState.Maximized;
            }
            else
            {
                // 进入全屏
                this.WindowStyle = WindowStyle.None;
                this.WindowState = WindowState.Maximized;
            }
        }

        #endregion

        #region 快捷操作按钮

        // 开始打印
        private void BtnStartPrint_Click(object sender, RoutedEventArgs e)
        {
            if (isPrinting && !isPaused)
            {
                ShowNotification("正在打印中...");
                return;
            }

            isPrinting = true;
            isPaused = false;
            timer.Start();
            UpdatePrintButtonState();

            AddLogEntry("转移打印开始执行", "#6366F1");
            ShowNotification("打印任务已启动");
        }

        // 暂停打印
        private void BtnPausePrint_Click(object sender, RoutedEventArgs e)
        {
            if (!isPrinting)
            {
                ShowNotification("请先开始打印");
                return;
            }

            isPaused = true;
            timer.Stop();
            UpdatePrintButtonState();

            AddLogEntry("打印任务已暂停", "#F59E0B");
            ShowNotification("打印已暂停，按Space继续");
        }

        // 继续打印
        private void ResumePrint()
        {
            if (!isPrinting || !isPaused)
                return;

            isPaused = false;
            timer.Start();
            UpdatePrintButtonState();

            AddLogEntry("打印任务已恢复", "#10B981");
            ShowNotification("打印已恢复");
        }

        // 紧急停止
        private void BtnEmergencyStop_Click(object sender, RoutedEventArgs e)
        {
            isPrinting = false;
            isPaused = false;
            timer.Stop();
            UpdatePrintButtonState();

            AddLogEntry("⚠️ 紧急停止触发！", "#EF4444");
            ShowNotification("⚠️ 紧急停止！请检查设备状态");

            // 播放警告效果
            FlashWarning();
        }

        // 停机调试
        private void BtnDebug_Click(object sender, RoutedEventArgs e)
        {
            AddLogEntry("进入停机调试模式", "#9CA3AF");
            ShowNotification("已进入调试模式");
        }

        // 清空日志
        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogPanel.Children.Clear();
            AddLogEntry("日志已清空", "#9CA3AF");
        }

        // 取消退出
        private void BtnCancelExit_Click(object sender, RoutedEventArgs e)
        {
            // 切换到第一个Tab
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

        #region 右键菜单

        private void MenuCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string data = $"批次: {TxtCompletedBatch.Text}\n" +
                              $"进度: {TxtProgress.Text}\n" +
                              $"间隙: {TxtGap.Text}\n" +
                              $"气压: {TxtPressure.Text}\n" +
                              $"功率: {TxtLaserPower.Text}";

                Clipboard.SetText(data);
                ShowNotification("数据已复制到剪贴板");
            }
            catch (Exception ex)
            {
                ShowNotification("复制失败: " + ex.Message);
            }
        }

        private void MenuExportLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logs = "";
                foreach (TextBlock tb in GetLogTextBlocks())
                {
                    logs += tb.Text + "\n";
                }

                string fileName = $"export_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                System.IO.File.WriteAllText(Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + "\\" + fileName, logs);

                ShowNotification($"日志已导出到桌面: {fileName}");
            }
            catch (Exception ex)
            {
                ShowNotification("导出失败: " + ex.Message);
            }
        }

        #endregion

        #region 退出系统

        // 退出系统（兼容原有事件）
        private void BtnExit_MouseLeftButtonDown(object sender, RoutedEventArgs e)
        {
            ShowExitConfirmation();
        }

        private void ShowExitConfirmation()
        {
            // 如果正在打印，显示警告
            if (isPrinting)
            {
                var result = MessageBox.Show(
                    "警告：打印任务正在进行中！\n\n确定要强制退出吗？",
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
                    "确定要退出控制系统吗？",
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

        // 初始化定时器
        private void InitializeTimer()
        {
            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += Timer_Tick;
        }

        // 定时器事件
        private void Timer_Tick(object sender, EventArgs e)
        {
            if (!isPaused)
            {
                elapsedSeconds++;
                UpdateRuntimeDisplay();

                // 模拟进度增长
                if (isPrinting && ProgressBarPrint.Value < 100)
                {
                    ProgressBarPrint.Value += 0.1;
                    TxtProgress.Text = $"{ProgressBarPrint.Value:F0}%";

                    // 模拟批次完成
                    if (ProgressBarPrint.Value >= (int.Parse(TxtCompletedBatch.Text.Split('/')[0].Trim()) % 200 + 1) * 0.5)
                    {
                        int currentBatch = int.Parse(TxtCompletedBatch.Text.Split('/')[0].Trim());
                        if (currentBatch < 200)
                        {
                            TxtCompletedBatch.Text = (currentBatch + 1).ToString();
                        }
                    }
                }
            }
        }

        // 更新运行时长显示
        private void UpdateRuntimeDisplay()
        {
            TimeSpan time = TimeSpan.FromSeconds(elapsedSeconds);
            TxtRunTime.Text = time.ToString(@"hh\:mm\:ss");
        }

        // 更新打印按钮状态
        private void UpdatePrintButtonState()
        {
            if (isPrinting && !isPaused)
            {
                BtnStartPrint.IsEnabled = false;
                BtnPausePrint.Content = "⏸ 暂停打印";
                BtnPausePrint.IsEnabled = true;
            }
            else if (isPrinting && isPaused)
            {
                BtnStartPrint.IsEnabled = false;
                BtnPausePrint.Content = "▶ 继续打印";
                BtnPausePrint.IsEnabled = true;
            }
            else
            {
                BtnStartPrint.IsEnabled = true;
                BtnPausePrint.Content = "⏸ 暂停打印";
                BtnPausePrint.IsEnabled = false;
            }
        }

        // 添加日志条目
        private void AddLogEntry(string message, string color)
        {
            TextBlock newEntry = new TextBlock
            {
                Text = $"[{DateTime.Now:HH:mm:ss}] {message}",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 6, 0, 0)
            };

            LogPanel.Children.Insert(0, newEntry);

            // 限制日志数量
            while (LogPanel.Children.Count > 50)
            {
                LogPanel.Children.RemoveAt(LogPanel.Children.Count - 1);
            }
        }

        // 获取日志文本块列表
        private System.Collections.Generic.List<TextBlock> GetLogTextBlocks()
        {
            var textBlocks = new System.Collections.Generic.List<TextBlock>();
            foreach (TextBlock tb in LogPanel.Children)
            {
                textBlocks.Add(tb);
            }
            return textBlocks;
        }

        // 显示通知（使用MessageBox简单实现）
        private void ShowNotification(string message)
        {
            // 在实际应用中，这里可以实现更优雅的通知UI
            // 目前使用状态栏提示替代
        }

        // 警告闪烁效果
        private void FlashWarning()
        {
            // 简单的视觉反馈
            MessageBox.Show(
                "⚠️ 紧急停止已触发！\n\n请立即检查设备状态。",
                "紧急停止",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        #endregion
    }
}
