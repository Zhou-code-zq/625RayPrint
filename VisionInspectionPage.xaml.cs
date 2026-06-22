using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading;
using System.Runtime.InteropServices;
using MvCamCtrl.NET;

namespace WpfApp1
{
    public partial class VisionInspectionPage : Page
    {
        // 海康SDK相机对象
        private MvCamera _camera = new MvCamera();
        private bool _isConnected = false;
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
            try
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
            catch (Exception ex)
            {
                Log("加载配置异常: " + ex.Message);
            }
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
                
                // 枚举设备 (GIGE和USB接口)
                int nRet = MvCamera.MV_CC_EnumDevices_NET(MvCamera.MV_GIGE_DEVICE | MvCamera.MV_USB_DEVICE, out MV_CC_DEVICE_INFO_LIST deviceList);
                
                if (nRet != 0 || deviceList.nDeviceNum == 0)
                {
                    Log("错误: 未找到相机设备，请检查相机连接");
                    MessageBox.Show("未找到相机设备，请检查相机连接！", "连接失败", 
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Log("找到 " + deviceList.nDeviceNum + " 个设备");

                // 获取第一个设备的信息
                IntPtr pDeviceInfo = Marshal.UnsafeAddrOfPinnedArrayElement(deviceList.pDeviceInfo, 0);
                MV_CC_DEVICE_INFO deviceInfo = (MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MV_CC_DEVICE_INFO));
                
                // 读取序列号
                string serialNumber = System.Text.Encoding.ASCII.GetString(deviceInfo.nSerialNum).Trim('\0');
                Log("设备序列号: " + serialNumber);

                Dispatcher.Invoke(() =>
                {
                    SerialNoText.Text = "序列号: " + serialNumber;
                    StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF9800"));
                    CameraStatusText.Text = "正在连接...";
                });

                // 创建设备句柄
                nRet = _camera.MV_CC_CreateHandle_NET(pDeviceInfo);
                if (nRet != 0)
                {
                    Log("错误: 创建设备句柄失败，错误码: 0x" + nRet.ToString("X8"));
                    return;
                }

                // 打开设备
                nRet = _camera.MV_CC_OpenDevice_NET(MvCamera.MV_ACCESS_Mode.MV_ACCESS_Exclusive, 0);
                if (nRet != 0)
                {
                    Log("错误: 打开设备失败，错误码: 0x" + nRet.ToString("X8"));
                    _camera.MV_CC_DestroyHandle_NET();
                    return;
                }

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
                MessageBox.Show("连接异常: " + ex.Message + "\n\n请确保：\n1. 已安装海康机器视觉SDK (MVS)\n2. 项目平台目标设置为 x64", 
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
                Log("错误: 相机未连接");
                return;
            }

            try
            {
                Log("正在开始采集...");

                // 设置采集模式为连续采集
                _camera.MV_CC_SetEnumValue_NET("AcquisitionMode", (uint)MvCamera.MV_CAM_ACQUISITION_MODE.MV_ACQ_MODE_CONTINUOUS);

                // 开始采集
                int nRet = _camera.MV_CC_StartGrabbing_NET();
                if (nRet != 0)
                {
                    Log("错误: 开始采集失败，错误码: 0x" + nRet.ToString("X8"));
                    return;
                }

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
            MV_FRAME_OUT frameOut = new MV_FRAME_OUT();
            IntPtr pFrameInfo = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MV_FRAME_OUT)));
            
            while (_isGrabbing && !_stopEvent.WaitOne(0))
            {
                try
                {
                    // 获取图像
                    int nRet = _camera.MV_CC_GetImageBuffer_NET(pFrameInfo, 1000);
                    
                    if (nRet == 0 && pFrameInfo != IntPtr.Zero)
                    {
                        frameOut = (MV_FRAME_OUT)Marshal.PtrToStructure(pFrameInfo, typeof(MV_FRAME_OUT));
                        
                        // 转换为BitmapSource
                        BitmapSource bitmapSource = ConvertToBitmapSource(frameOut);
                        
                        // 更新UI
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            CameraImage.Source = bitmapSource;
                        }));
                        
                        // 释放图像缓存
                        _camera.MV_CC_FreeImageBuffer_NET(pFrameInfo);
                    }
                }
                catch (Exception)
                {
                    // 忽略采集过程中的异常
                }
            }
            
            Marshal.FreeHGlobal(pFrameInfo);
        }

        private BitmapSource ConvertToBitmapSource(MV_FRAME_OUT frameOut)
        {
            try
            {
                int width = (int)frameOut.nWidth;
                int height = (int)frameOut.nHeight;
                IntPtr pData = frameOut.pBufAddr;
                
                if (pData == IntPtr.Zero || width <= 0 || height <= 0)
                {
                    return null;
                }

                // 判断像素格式
                if (frameOut.enPixelType == MvCamera.PixelType_Gvsp_Mono8)
                {
                    // 灰度图像
                    var bitmap = BitmapSource.Create(width, height, 96, 96, 
                        PixelFormats.Gray8, null, pData, width * height, width);
                    bitmap.Freeze();
                    return bitmap;
                }
                else
                {
                    // 彩色图像 (Mono8转Bgr24)
                    int bytesPerPixel = 3;
                    byte[] rgbData = new byte[width * height * bytesPerPixel];
                    
                    for (int i = 0; i < width * height; i++)
                    {
                        rgbData[i * bytesPerPixel] = Marshal.ReadByte(pData, i);     // B
                        rgbData[i * bytesPerPixel + 1] = Marshal.ReadByte(pData, i); // G
                        rgbData[i * bytesPerPixel + 2] = Marshal.ReadByte(pData, i); // R
                    }
                    
                    var bitmap = BitmapSource.Create(width, height, 96, 96, 
                        PixelFormats.Bgr24, null, rgbData, width * bytesPerPixel);
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch
            {
                return null;
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

                _camera.MV_CC_StopGrabbing_NET();

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

                // 关闭设备
                if (_isConnected)
                {
                    _camera.MV_CC_CloseDevice_NET();
                    _camera.MV_CC_DestroyHandle_NET();
                    _isConnected = false;
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
            
            // 设置相机曝光时间 (微秒)
            if (_isConnected)
            {
                try
                {
                    _camera.MV_CC_SetFloatValue_NET("ExposureTime", (float)(value * 1000));
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
            if (_isConnected)
            {
                try
                {
                    _camera.MV_CC_SetFloatValue_NET("Gain", (float)value);
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
                
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (LogText.Text.Length > 10000)
                    {
                        LogText.Text = LogText.Text.Substring(LogText.Text.Length - 5000);
                    }
                    LogText.Text += logEntry;
                    LogText.ScrollToEnd();
                }));
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
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            CurrentTimeText.Text = "当前时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        }));
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
