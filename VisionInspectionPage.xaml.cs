using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading;
using MVS;

namespace WpfApp1
{
    public partial class VisionInspectionPage : Page
    {
        // 海康SDK相机对象
        private Camera _camera = null;
        private bool _isGrabbing = false;
        private Thread _grabThread = null;
        private ManualResetEvent _stopEvent = new ManualResetEvent(false);
        
        // 相机配置信息
        private string _cameraSerial = "";
        private string _cameraIP = "";

        public VisionInspectionPage()
        {
            InitializeComponent();
            UpdateTime();
            Loaded += VisionInspectionPage_Loaded;
        }

        private void VisionInspectionPage_Loaded(object sender, RoutedEventArgs e)
        {
            // 从配置页面获取相机信息
            var configPage = new ParameterConfigPage();
            configPage.LoadConfig();
            
            _cameraSerial = configPage.GetCameraSerial();
            _cameraIP = configPage.GetCameraIP();
            
            if (!string.IsNullOrEmpty(_cameraSerial))
            {
                SerialNoText.Text = "序列号: " + _cameraSerial;
            }
            else if (!string.IsNullOrEmpty(_cameraIP))
            {
                SerialNoText.Text = "IP地址: " + _cameraIP;
            }
            
            Log("页面加载完成，等待连接相机...");
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
                Log("正在枚举设备...");
                
                // 枚举设备
                DeviceList deviceList = Camera.EnumerateDevices();
                
                if (deviceList.Count == 0)
                {
                    Log("错误: 未找到相机设备，请检查相机连接");
                    MessageBox.Show("未找到相机设备，请检查相机连接！", "连接失败", 
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Log("找到 " + deviceList.Count + " 个设备");

                // 获取第一个设备
                DeviceInfo deviceInfo = deviceList[0];
                
                // 读取序列号
                string serialNumber = deviceInfo.SerialNumber;
                Log("设备序列号: " + serialNumber);

                Dispatcher.Invoke(() =>
                {
                    SerialNoText.Text = "序列号: " + serialNumber;
                    StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF9800"));
                    CameraStatusText.Text = "正在连接...";
                });

                // 创建相机对象并连接
                _camera = new Camera(deviceInfo);
                _camera.Open();
                
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
                MessageBox.Show("连接异常: " + ex.Message + "\n\n请确保已安装海康机器视觉SDK (MVS)\n下载地址: https://www.hikvision.com/cn/support/tools/hikvision-tools/hikvision-mvs/", 
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnStartGrab_Click(object sender, RoutedEventArgs e)
        {
            StartGrabbing();
        }

        private void StartGrabbing()
        {
            if (_camera == null || !_camera.IsOpen)
            {
                Log("错误: 相机未连接");
                return;
            }

            try
            {
                Log("正在开始采集...");

                // 开始采集
                _camera.StartGrabbing();

                _isGrabbing = true;
                _stopEvent.Reset();

                // 启动采集线程
                _grabThread = new Thread(GrabThread);
                _grabThread.IsBackground = true;
                _grabThread.Start();

                Log("图像采集已开始");

                Dispatcher.Invoke(() =>
                {
                    BtnStartGrab.IsEnabled = false;
                    BtnStopGrab.IsEnabled = true;
                    CameraStatusText.Text = "正在采集";
                });

            }
            catch (Exception ex)
            {
                Log("开始采集异常: " + ex.Message);
            }
        }

        private void GrabThread()
        {
            while (_isGrabbing && !_stopEvent.WaitOne(0))
            {
                try
                {
                    // 获取图像
                    IGrabResult grabResult = _camera.Grab(1000);
                    
                    if (grabResult != null && grabResult.Image != null)
                    {
                        // 转换为WPF可用的图像
                        BitmapSource bitmapSource = ConvertToBitmapSource(grabResult);
                        
                        // 更新UI
                        Dispatcher.Invoke(() =>
                        {
                            CameraImage.Source = bitmapSource;
                        });
                    }
                }
                catch (Exception ex)
                {
                    // 忽略采集过程中的异常
                }
            }
        }

        private BitmapSource ConvertToBitmapSource(IGrabResult grabResult)
        {
            // 根据图像格式转换
            if (grabResult.Image.PixelFormat == PixelFormat.Mono8)
            {
                // 灰度图像
                var bitmap = new WriteableBitmap(
                    (int)grabResult.Image.Width,
                    (int)grabResult.Image.Height,
                    96, 96,
                    PixelFormats.Gray8,
                    null);
                
                bitmap.WritePixels(new Int32Rect(0, 0, 
                    (int)grabResult.Image.Width, 
                    (int)grabResult.Image.Height),
                    grabResult.Image.Buffer,
                    (int)grabResult.Image.Stride,
                    0);
                
                return bitmap;
            }
            else
            {
                // 彩色图像 (转换为首选项格式)
                var bitmap = new FormatConvertedBitmap();
                bitmap.BeginInit();
                bitmap.Source = grabResult.Image;
                bitmap.DestinationFormat = PixelFormats.Bgr24;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }

        private void BtnStopGrab_Click(object sender, RoutedEventArgs e)
        {
            StopGrabbing();
        }

        private void StopGrabbing()
        {
            if (!_isGrabbing) return;

            try
            {
                Log("正在停止采集...");
                
                _isGrabbing = false;
                _stopEvent.Set();

                if (_camera != null)
                {
                    _camera.StopGrabbing();
                }

                if (_grabThread != null && _grabThread.IsAlive)
                {
                    _grabThread.Join(2000);
                }

                Log("图像采集已停止");

                Dispatcher.Invoke(() =>
                {
                    BtnStartGrab.IsEnabled = true;
                    BtnStopGrab.IsEnabled = false;
                    CameraStatusText.Text = "相机已连接";
                });

            }
            catch (Exception ex)
            {
                Log("停止采集异常: " + ex.Message);
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
                Log("正在断开相机连接...");

                // 停止采集
                if (_isGrabbing)
                {
                    StopGrabbing();
                }

                // 关闭相机
                if (_camera != null && _camera.IsOpen)
                {
                    _camera.Close();
                    _camera.Dispose();
                    _camera = null;
                }

                Log("相机已断开连接");

                Dispatcher.Invoke(() =>
                {
                    StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#666"));
                    CameraStatusText.Text = "相机未连接";
                    SerialNoText.Text = "序列号: --";
                    BtnConnect.IsEnabled = true;
                    BtnStartGrab.IsEnabled = false;
                    BtnStopGrab.IsEnabled = false;
                    BtnDisconnect.IsEnabled = false;
                    CameraImage.Source = null;
                    PreviewPlaceholder.Visibility = Visibility.Visible;
                    PreviewPlaceholder.Text = "点击「连接相机」开始预览";
                });

            }
            catch (Exception ex)
            {
                Log("断开连接异常: " + ex.Message);
            }
        }

        private void ExposureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ExposureValueText == null) return;
            
            double value = ExposureSlider.Value;
            ExposureValueText.Text = value.ToString("F1") + " ms";
            
            // 设置相机曝光时间
            if (_camera != null && _camera.IsOpen)
            {
                try
                {
                    _camera.ExposureTime.SetValue(value * 1000); // 转换为微秒
                }
                catch { }
            }
        }

        private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (GainValueText == null) return;
            
            double value = GainSlider.Value;
            GainValueText.Text = value.ToString("F1") + " dB";
            
            // 设置相机增益
            if (_camera != null && _camera.IsOpen)
            {
                try
                {
                    _camera.Gain.SetValue(value);
                }
                catch { }
            }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogText.Text = "";
            Log("日志已清空");
        }

        private void Log(string message)
        {
            try
            {
                string time = DateTime.Now.ToString("HH:mm:ss");
                string logEntry = "[" + time + "] " + message + "\n";
                
                Dispatcher.Invoke(() =>
                {
                    if (LogText.Text.Length > 10000)
                    {
                        LogText.Text = LogText.Text.Substring(LogText.Text.Length - 5000);
                    }
                    LogText.Text += logEntry;
                    LogText.ScrollToEnd();
                });
            }
            catch { }
        }

        private void UpdateTime()
        {
            Thread timeThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        Thread.Sleep(1000);
                        Dispatcher.Invoke(() =>
                        {
                            CurrentTimeText.Text = "当前时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        });
                    }
                    catch
                    {
                        break;
                    }
                }
            });
            timeThread.IsBackground = true;
            timeThread.Start();
        }

        public void SetCameraInfo(string serial, string ip)
        {
            _cameraSerial = serial;
            _cameraIP = ip;
            
            if (!string.IsNullOrEmpty(_cameraSerial))
            {
                SerialNoText.Text = "序列号: " + _cameraSerial;
            }
            else if (!string.IsNullOrEmpty(_cameraIP))
            {
                SerialNoText.Text = "IP地址: " + _cameraIP;
            }
        }
    }
}
