using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading;
using System.Runtime.InteropServices;

namespace WpfApp1
{
    public partial class VisionInspectionPage : Page
    {
        // 相机状态
        private bool _isConnected = false;
        private bool _isGrabbing = false;
        private Thread _grabThread = null;
        private ManualResetEvent _stopEvent = new ManualResetEvent(false);
        private WriteableBitmap _writeableBitmap = null;
        private Random _random = new Random();
        
        // 相机参数
        private double _exposureTime = 20.0;
        private double _gainValue = 10.0;

        public VisionInspectionPage()
        {
            InitializeComponent();
        }

        // ========== 相机操作方法 ==========

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            ConnectCamera();
        }

        private void ConnectCamera()
        {
            try
            {
                Log("正在连接相机...");
                
                // 模拟相机连接（实际使用时替换为真实SDK调用）
                System.Threading.Thread.Sleep(500);
                
                _isConnected = true;
                Log("相机连接成功！");

                Dispatcher.Invoke(() =>
                {
                    StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50"));
                    CameraStatusText.Text = "相机已连接";
                    BtnConnect.IsEnabled = false;
                    BtnStartGrab.IsEnabled = true;
                    BtnStopGrab.IsEnabled = false;
                    BtnDisconnect.IsEnabled = true;
                    PreviewPlaceholder.Visibility = Visibility.Collapsed;
                });

            }
            catch (Exception ex)
            {
                Log("连接异常: " + ex.Message);
                MessageBox.Show("连接异常: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnStartGrab_Click(object sender, RoutedEventArgs e)
        {
            StartGrabbing();
        }

        private void StartGrabbing()
        {
            if (!_isConnected)
            {
                Log("错误: 请先连接相机");
                return;
            }

            try
            {
                Log("开始采集图像...");
                _isGrabbing = true;
                _stopEvent.Reset();

                // 初始化显示
                Dispatcher.Invoke(() =>
                {
                    BtnConnect.IsEnabled = false;
                    BtnStartGrab.IsEnabled = false;
                    BtnStopGrab.IsEnabled = true;
                    BtnDisconnect.IsEnabled = false;
                    CameraStatusText.Text = "正在采集...";
                });

                // 创建采集线程
                _grabThread = new Thread(GrabThread);
                _grabThread.IsBackground = true;
                _grabThread.Start();

                Log("图像采集中...");
            }
            catch (Exception ex)
            {
                Log("开始采集失败: " + ex.Message);
            }
        }

        private void GrabThread()
        {
            try
            {
                // 初始化图像
                int width = 800;
                int height = 450;
                Dispatcher.Invoke(() =>
                {
                    _writeableBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
                    CameraImage.Source = _writeableBitmap;
                });

                while (_isGrabbing && !_stopEvent.WaitOne(33))
                {
                    // 生成模拟图像
                    byte[] imageData = GenerateSimulatedImage(width, height);
                    
                    // 显示图像
                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            _writeableBitmap.Lock();
                            Marshal.Copy(imageData, 0, _writeableBitmap.BackBuffer, imageData.Length);
                            _writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                            _writeableBitmap.Unlock();
                        }
                        catch { }
                    });

                    // 更新统计
                    Dispatcher.Invoke(() =>
                    {
                        FrameCountText.Text = "帧数: " + (int.Parse(FrameCountText.Text.Replace("帧数: ", "")) + 1);
                    });
                }
            }
            catch (Exception ex)
            {
                Log("采集线程异常: " + ex.Message);
            }
        }

        private byte[] GenerateSimulatedImage(int width, int height)
        {
            byte[] data = new byte[width * height * 3];
            
            // 生成深蓝色背景
            for (int i = 0; i < data.Length; i += 3)
            {
                data[i] = 30;      // B
                data[i + 1] = 30;  // G
                data[i + 2] = 50;  // R
            }

            // 添加一些随机条纹
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int offset = (y * width + x) * 3;
                    if (y % 30 < 15)
                    {
                        data[offset] = 80;
                        data[offset + 1] = 80;
                        data[offset + 2] = 120;
                    }
                }
            }

            return data;
        }

        private void BtnStopGrab_Click(object sender, RoutedEventArgs e)
        {
            StopGrabbing();
        }

        private void StopGrabbing()
        {
            try
            {
                Log("停止采集...");
                _isGrabbing = false;
                _stopEvent.Set();

                if (_grabThread != null && _grabThread.IsAlive)
                {
                    _grabThread.Join(1000);
                }

                Dispatcher.Invoke(() =>
                {
                    BtnConnect.IsEnabled = false;
                    BtnStartGrab.IsEnabled = true;
                    BtnStopGrab.IsEnabled = false;
                    BtnDisconnect.IsEnabled = true;
                    CameraStatusText.Text = "采集已停止";
                });

                Log("采集已停止");
            }
            catch (Exception ex)
            {
                Log("停止采集失败: " + ex.Message);
            }
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            DisconnectCamera();
        }

        private void DisconnectCamera()
        {
            try
            {
                if (_isGrabbing)
                {
                    StopGrabbing();
                }

                Log("正在断开相机连接...");

                // 模拟断开
                System.Threading.Thread.Sleep(200);

                _isConnected = false;

                Dispatcher.Invoke(() =>
                {
                    StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E9E9E"));
                    CameraStatusText.Text = "已断开";
                    CameraImage.Source = null;
                    BtnConnect.IsEnabled = true;
                    BtnStartGrab.IsEnabled = false;
                    BtnStopGrab.IsEnabled = false;
                    BtnDisconnect.IsEnabled = false;
                    PreviewPlaceholder.Text = "相机已断开";
                    PreviewPlaceholder.Visibility = Visibility.Visible;
                });

                Log("相机已断开连接");
            }
            catch (Exception ex)
            {
                Log("断开连接异常: " + ex.Message);
            }
        }

        // ========== 参数设置方法 ==========

        private void ExposureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ExposureValueText != null)
            {
                _exposureTime = e.NewValue;
                ExposureValueText.Text = _exposureTime.ToString("F1") + " ms";
                
                if (_isConnected)
                {
                    Log("曝光时间已设置为: " + _exposureTime.ToString("F1") + " ms");
                }
            }
        }

        private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (GainValueText != null)
            {
                _gainValue = e.NewValue;
                GainValueText.Text = _gainValue.ToString("F1") + " dB";
                
                if (_isConnected)
                {
                    Log("增益已设置为: " + _gainValue.ToString("F1") + " dB");
                }
            }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            if (LogText != null)
            {
                LogText.Text = "";
                Log("日志已清空");
            }
        }

        // ========== 辅助方法 ==========

        private void Log(string message)
        {
            try
            {
                string time = DateTime.Now.ToString("HH:mm:ss");
                Dispatcher.Invoke(() =>
                {
                    if (LogText != null)
                    {
                        LogText.Text += "[" + time + "] " + message + "\n";
                        LogScrollViewer.ScrollToEnd();
                    }
                });
            }
            catch { }
        }

        public void SetCameraInfo(string serial, string ip)
        {
            Dispatcher.Invoke(() =>
            {
                SerialNoText.Text = "序列号: " + serial;
                IpText.Text = "IP: " + ip;
            });
        }
    }
}
