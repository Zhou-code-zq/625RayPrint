using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WpfApp1
{
    public partial class VisionInspectionPage : UserControl
    {
        private DispatcherTimer _timer;
        private bool _isConnected = false;
        private bool _isGrabbing = false;

        public VisionInspectionPage()
        {
            InitializeComponent();
            InitTimer();
            UpdateTime();
        }

        private void InitTimer()
        {
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (s, e) => UpdateTime();
            _timer.Start();
        }

        private void UpdateTime()
        {
            CurrentTimeText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private void AddLog(string message, string color = "#10B981")
        {
            var logText = new TextBlock
            {
                Text = $"[{DateTime.Now:HH:mm:ss}] {message}",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0)
            };
            LogPanel.Children.Add(logText);
        }

        private void UpdateCameraStatus(bool connected, string statusText)
        {
            _isConnected = connected;
            CameraStatusText.Text = statusText;
            if (connected)
            {
                CameraStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
            }
            else
            {
                CameraStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
            }
        }

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected)
            {
                AddLog("相机已连接，请先断开", "#F59E0B");
                return;
            }

            AddLog("正在连接海康相机...");
            // 模拟连接过程
            BtnConnect.IsEnabled = false;
            
            // 实际使用时，这里应该调用海康相机的SDK进行连接
            // MVS SDK: MV_CC_CreateHandle, MV_CC_OpenDevice 等
            string serialNo = SerialNoText.Text;
            if (string.IsNullOrWhiteSpace(serialNo) || serialNo == "待配置")
            {
                AddLog("连接失败: 请先在参数配置中设置相机序列号", "#EF4444");
                BtnConnect.IsEnabled = true;
                return;
            }

            // 模拟连接成功
            System.Threading.Thread.Sleep(500);
            _isConnected = true;
            UpdateCameraStatus(true, "相机已连接");
            AddLog($"相机连接成功 (序列号: {serialNo})", "#10B981");
            BtnConnect.IsEnabled = false;
            BtnStartGrab.IsEnabled = true;
            BtnDisconnect.IsEnabled = true;
        }

        private void BtnStartGrab_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                AddLog("请先连接相机", "#F59E0B");
                return;
            }

            if (_isGrabbing)
            {
                AddLog("相机已在采集中", "#F59E0B");
                return;
            }

            AddLog("开始图像采集...");
            _isGrabbing = true;
            BtnStartGrab.IsEnabled = false;
            BtnStopGrab.IsEnabled = true;
            AddLog("图像采集已启动", "#10B981");
        }

        private void BtnStopGrab_Click(object sender, RoutedEventArgs e)
        {
            if (!_isGrabbing)
            {
                AddLog("相机未在采集", "#F59E0B");
                return;
            }

            AddLog("停止图像采集...");
            _isGrabbing = false;
            BtnStartGrab.IsEnabled = true;
            BtnStopGrab.IsEnabled = false;
            AddLog("图像采集已停止", "#F59E0B");
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

        /// <summary>
        /// 设置相机信息（供外部调用）
        /// </summary>
        public void SetCameraInfo(string serialNo, string ip)
        {
            SerialNoText.Text = serialNo;
            IpText.Text = ip;
            if (!string.IsNullOrWhiteSpace(serialNo))
            {
                AddLog($"相机信息已更新: {serialNo}", "#635BFF");
            }
        }
    }
}
