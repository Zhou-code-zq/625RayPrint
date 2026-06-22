using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Media;
using System.IO;
using System.Configuration;

namespace WpfApp1
{
    public partial class VisionInspectionPage : UserControl
    {
        private bool _isConnected = false;
        private bool _isGrabbing = false;
        private System.Windows.Threading.DispatcherTimer _timer;
        private Random _random = new Random();

        public VisionInspectionPage()
        {
            InitializeComponent();
            InitializeTimer();
            LoadCameraConfig();
        }

        private void InitializeTimer()
        {
            _timer = new System.Windows.Threading.DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            CurrentTimeText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private void LoadCameraConfig()
        {
            try
            {
                string configPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "camera_config.ini");
                if (File.Exists(configPath))
                {
                    string serialNo = GetConfigValue(configPath, "SerialNo", "");
                    string ip = GetConfigValue(configPath, "IP", "");
                    SerialNoText.Text = string.IsNullOrEmpty(serialNo) ? "待配置" : serialNo;
                    IpText.Text = string.IsNullOrEmpty(ip) ? "待配置" : ip;
                    AddLog("已加载相机配置");
                }
                else
                {
                    SerialNoText.Text = "待配置";
                    IpText.Text = "待配置";
                    AddLog("未找到配置文件，请先在参数配置页面设置");
                }
            }
            catch (Exception ex)
            {
                AddLog("加载配置失败: " + ex.Message);
            }
        }

        // 供外部调用的方法，用于更新相机信息
        public void SetCameraInfo(string serialNo, string ipAddress)
        {
            try
            {
                SerialNoText.Text = string.IsNullOrEmpty(serialNo) ? "待配置" : serialNo;
                IpText.Text = string.IsNullOrEmpty(ipAddress) ? "待配置" : ipAddress;
                AddLog("相机信息已更新");
            }
            catch (Exception ex)
            {
                AddLog("更新相机信息失败: " + ex.Message);
            }
        }

        private string GetConfigValue(string path, string key, string defaultValue)
        {
            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    if (line.StartsWith(key + "="))
                    {
                        return line.Substring(key.Length + 1).Trim();
                    }
                }
            }
            catch { }
            return defaultValue;
        }

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            string serialNo = SerialNoText.Text;
            string ip = IpText.Text;

            if (serialNo == "待配置" && ip == "待配置")
            {
                AddLog("请先配置相机参数", "#EF4444");
                MessageBox.Show("请先在参数配置页面设置相机连接信息", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            AddLog("正在连接相机...");

            // 模拟连接过程
            System.Threading.Thread.Sleep(1000);

            _isConnected = true;
            _isGrabbing = false;
            UpdateCameraStatus(true, "相机已连接");

            AddLog("相机连接成功", "#10B981");

            BtnConnect.IsEnabled = false;
            BtnStartGrab.IsEnabled = true;
            BtnStopGrab.IsEnabled = false;
            BtnDisconnect.IsEnabled = true;
        }

        private void BtnStartGrab_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                AddLog("请先连接相机", "#F59E0B");
                return;
            }

            AddLog("开始采集图像...");
            _isGrabbing = true;
            UpdateCameraStatus(true, "采集中");

            BtnStartGrab.IsEnabled = false;
            BtnStopGrab.IsEnabled = true;

            AddLog("开始采集成功", "#10B981");
        }

        private void BtnStopGrab_Click(object sender, RoutedEventArgs e)
        {
            if (!_isGrabbing)
            {
                AddLog("相机未在采集", "#F59E0B");
                return;
            }

            AddLog("停止采集...");
            _isGrabbing = false;
            UpdateCameraStatus(true, "相机已连接");

            BtnStartGrab.IsEnabled = true;
            BtnStopGrab.IsEnabled = false;

            AddLog("停止采集成功", "#10B981");
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                AddLog("相机未连接", "#F59E0B");
                return;
            }

            if (_isGrabbing)
            {
                AddLog("请先停止采集", "#F59E0B");
                MessageBox.Show("请先停止采集", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AddLog("正在断开相机连接...");
            _isConnected = false;
            _isGrabbing = false;
            UpdateCameraStatus(false, "相机未连接");
            AddLog("相机已断开连接", "#F59E0B");

            BtnConnect.IsEnabled = true;
            BtnStartGrab.IsEnabled = false;
            BtnStopGrab.IsEnabled = false;
            BtnDisconnect.IsEnabled = false;
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogPanel.Children.Clear();
            AddLog("日志已清空");
        }

        private void ExposureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ExposureValueText != null)
            {
                ExposureValueText.Text = ((int)ExposureSlider.Value).ToString();
            }
        }

        private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (GainValueText != null)
            {
                GainValueText.Text = GainSlider.Value.ToString("F1");
            }
        }

        private void UpdateCameraStatus(bool connected, string status)
        {
            _isConnected = connected;
            CameraStatusText.Text = status;
            CameraStatusText.Foreground = new SolidColorBrush(connected ? Color.FromRgb(0x10, 0xB9, 0x81) : Color.FromRgb(0x66, 0x66, 0x66));
            ConnectionStatusText.Text = connected ? "已连接" : "未连接";
            ConnectionStatusText.Foreground = new SolidColorBrush(connected ? Color.FromRgb(0x10, 0xB9, 0x81) : Color.FromRgb(0xEF, 0x44, 0x44));
        }

        private void AddLog(string message, string colorHex = "#10B981")
        {
            try
            {
                string time = DateTime.Now.ToString("HH:mm:ss");
                TextBlock log = new TextBlock
                {
                    Text = $"[{time}] {message}",
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)),
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 2)
                };
                LogPanel.Children.Add(log);

                // 滚动到底部
                var scrollViewer = FindParent<ScrollViewer>(LogPanel);
                if (scrollViewer != null)
                {
                    scrollViewer.ScrollToEnd();
                }

                // 限制日志数量
                while (LogPanel.Children.Count > 100)
                {
                    LogPanel.Children.RemoveAt(0);
                }
            }
            catch { }
        }

        private static T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parent = VisualTreeHelper.GetParent(child);
            while (parent != null && !(parent is T))
            {
                parent = VisualTreeHelper.GetParent(parent);
            }
            return parent as T;
        }
    }
}
