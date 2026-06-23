using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

using MvCameraControl;
using MvCameraControl.Device;

namespace WpfApp1
{
    public partial class VisionInspectionPage : Page
    {
        // 相机对象 - 使用SDK 4.5.0+的新API
        private IDevice _device = null;
        private bool _isConnected = false;
        private bool _isGrabbing = false;
        private Thread _grabThread = null;
        private IntPtr _imageBuffer = IntPtr.Zero;
        private int _imageWidth = 2448;
        private int _imageHeight = 2048;

        // 相机参数
        private string _serialNo = "";
        private string _ipAddress = "";
        private int _deviceType = 0;  // 0=MV系列(GenTL), 1=CA系列(GigE), 2=CH系列(USB)

        public VisionInspectionPage()
        {
            InitializeComponent();
            this.Loaded += VisionInspectionPage_Loaded;
        }

        private void VisionInspectionPage_Loaded(object sender, RoutedEventArgs e)
        {
            // 从参数配置页面获取相机信息
            LoadCameraInfo();
        }

        // 从参数配置页面加载相机信息
        private void LoadCameraInfo()
        {
            int deviceType = 0;
            string serialNo = "";
            string ipAddress = "";

            Application.Current.Dispatcher.Invoke(() =>
            {
                if (MainWindow.CameraConfig != null)
                {
                    deviceType = MainWindow.CameraConfig.DeviceType;
                    serialNo = MainWindow.CameraConfig.SerialNo;
                    ipAddress = MainWindow.CameraConfig.IpAddress;
                }
            });

            _deviceType = deviceType;
            _serialNo = serialNo;
            _ipAddress = ipAddress;

            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                string deviceName;
                switch (deviceType)
                {
                    case 0:
                        deviceName = "MV系列";
                        break;
                    case 1:
                        deviceName = "CA系列";
                        break;
                    case 2:
                        deviceName = "CH系列";
                        break;
                    default:
                        deviceName = "未知";
                        break;
                }
                AddLog($"已加载配置：{deviceName} | {(!string.IsNullOrEmpty(serialNo) ? "序列号:" + serialNo : "IP:" + ipAddress)}");
            }));
        }

        // 连接相机按钮
        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected)
            {
                AddLog("相机已连接，请先断开");
                return;
            }
            
            // 如果没有配置信息，弹出提示
            if (string.IsNullOrEmpty(_serialNo) && string.IsNullOrEmpty(_ipAddress))
            {
                MessageBox.Show("请先在\"参数配置\"页面设置相机参数并保存！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            ConnectCamera();
        }

        // 连接相机
        private void ConnectCamera()
        {
            try
            {
                AddLog("正在枚举设备...");
                
                // 根据设备类型确定设备层
                DeviceTLayerType deviceTLayer;
                string deviceTypeName;
                
                switch (_deviceType)
                {
                    case 0:  // MV系列 -> GenTL虚拟相机
                        deviceTLayer = DeviceTLayerType.MvGenTLDevice;
                        deviceTypeName = "GenTL 虚拟相机";
                        break;
                    case 1:  // CA系列 -> GigE工业相机
                        deviceTLayer = DeviceTLayerType.MvGigEDevice;
                        deviceTypeName = "GigE 工业相机";
                        break;
                    case 2:  // CH系列 -> USB相机
                        deviceTLayer = DeviceTLayerType.MvUsbDevice;
                        deviceTypeName = "USB 相机";
                        break;
                    default:
                        deviceTLayer = DeviceTLayerType.MvGenTLDevice;
                        deviceTypeName = "默认(GenTL)";
                        break;
                }
                
                AddLog($"设备类型: {deviceTypeName}");

                // 枚举设备 - 使用新API
                List<IDeviceInfo> deviceInfoList = null;
                int nRet = DeviceEnumerator.EnumDevices(deviceTLayer, out deviceInfoList);
                
                if (nRet != 0 || deviceInfoList == null || deviceInfoList.Count == 0)
                {
                    AddLog($"未发现 {deviceTypeName} 设备");
                    return;
                }

                AddLog($"发现 {deviceInfoList.Count} 个 {deviceTypeName} 设备");

                // 根据序列号或IP地址查找目标设备
                IDeviceInfo targetDevice = null;
                for (int i = 0; i < deviceInfoList.Count; i++)
                {
                    IDeviceInfo info = deviceInfoList[i];
                    
                    // 检查是否匹配
                    bool match = false;
                    if (!string.IsNullOrEmpty(_serialNo))
                    {
                        // 根据序列号匹配
                        if (info.SerialNumber == _serialNo)
                        {
                            match = true;
                        }
                    }
                    else if (!string.IsNullOrEmpty(_ipAddress))
                    {
                        // 根据IP地址匹配
                        if (info/ipAddress == _ipAddress)
                        {
                            match = true;
                        }
                    }
                    
                    if (match)
                    {
                        targetDevice = info;
                        AddLog($"找到匹配的设备: {info.SerialNumber}");
                        break;
                    }
                }

                // 如果没找到指定设备，使用第一个
                if (targetDevice == null)
                {
                    targetDevice = deviceInfoList[0];
                    AddLog($"使用第一个可用设备");
                }

                // 创建设备 - 使用新API
                _device = DeviceFactory.CreateDevice(targetDevice);
                if (_device == null)
                {
                    AddLog("创建设备失败!");
                    return;
                }

                // 打开设备 - 使用新API
                nRet = _device.Open();
                if (nRet != 0)
                {
                    AddLog($"打开设备失败，错误码: {nRet}");
                    _device = null;
                    return;
                }

                _isConnected = true;
                AddLog("相机连接成功！");

                // 更新UI
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    BtnConnect.Content = "断开相机";
                    BtnStartGrab.IsEnabled = true;
                    BtnStopGrab.IsEnabled = false;
                    StatusText.Text = "已连接";
                }));
            }
            catch (Exception ex)
            {
                AddLog($"连接异常: {ex.Message}");
            }
        }

        // 断开相机
        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                AddLog("相机未连接");
                return;
            }

            try
            {
                StopGrabbing();

                if (_device != null)
                {
                    _device.Close();
                    _device = null;
                }

                _isConnected = false;
                AddLog("相机已断开");

                // 更新UI
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    BtnConnect.Content = "连接相机";
                    BtnStartGrab.IsEnabled = false;
                    BtnStopGrab.IsEnabled = false;
                    StatusText.Text = "未连接";
                }));
            }
            catch (Exception ex)
            {
                AddLog($"断开异常: {ex.Message}");
            }
        }

        // 开始采集
        private void BtnStartGrab_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                AddLog("请先连接相机");
                return;
            }

            if (_isGrabbing)
            {
                AddLog("已经在采集中");
                return;
            }

            StartGrabbing();
        }

        // 开始采集
        private void StartGrabbing()
        {
            try
            {
                // 开始采集 - 新API
                int nRet = _device.StartGrabbing();
                if (nRet != 0)
                {
                    AddLog($"开始采集失败，错误码: {nRet}");
                    return;
                }

                _isGrabbing = true;
                AddLog("已开始采集");

                // 启动采集线程
                _grabThread = new Thread(GrabLoop);
                _grabThread.IsBackground = true;
                _grabThread.Start();

                // 更新UI
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    BtnStartGrab.IsEnabled = false;
                    BtnStopGrab.IsEnabled = true;
                }));
            }
            catch (Exception ex)
            {
                AddLog($"开始采集异常: {ex.Message}");
            }
        }

        // 停止采集
        private void BtnStopGrab_Click(object sender, RoutedEventArgs e)
        {
            StopGrabbing();
        }

        // 停止采集
        private void StopGrabbing()
        {
            try
            {
                if (_grabThread != null && _grabThread.IsAlive)
                {
                    _grabThread.Interrupt();
                    _grabThread.Join(1000);
                    _grabThread = null;
                }

                if (_device != null)
                {
                    _device.StopGrabbing();
                }

                _isGrabbing = false;
                AddLog("已停止采集");

                // 更新UI
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    BtnStartGrab.IsEnabled = true;
                    BtnStopGrab.IsEnabled = false;
                }));
            }
            catch (Exception ex)
            {
                AddLog($"停止采集异常: {ex.Message}");
            }
        }

        // 采集循环
        private void GrabLoop()
        {
            while (_isGrabbing && _isConnected)
            {
                try
                {
                    // 获取一帧图像 - 新API
                    IFrameOut frame = null;
                    int nRet = _device.GetOneFrameTimeout(ref frame, 1000);
                    
                    if (nRet == 0 && frame != null)
                    {
                        // 获取图像数据
                        IntPtr pData = frame.Image;
                        int nWidth = frame.Width;
                        int nHeight = frame.Height;
                        PixelType pixelType = frame.PixelType;

                        // 更新帧计数
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            FrameCountText.Text = (int.Parse(FrameCountText.Text) + 1).ToString();
                        }));

                        // 显示图像
                        if (pData != IntPtr.Zero)
                        {
                            ShowImage(pData, nWidth, nHeight);
                        }

                        // 释放帧
                        frame.Release();
                    }
                }
                catch (ThreadInterruptedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"采集异常: {ex.Message}");
                }

                Thread.Sleep(10);
            }
        }

        // 显示图像
        private void ShowImage(IntPtr pData, int nWidth, int nHeight)
        {
            try
            {
                // 更新图像尺寸
                if (nWidth > 0 && nHeight > 0)
                {
                    _imageWidth = nWidth;
                    _imageHeight = nHeight;
                }

                // 分配图像缓冲区
                int imageSize = _imageWidth * _imageHeight * 3;  // RGB24
                if (_imageBuffer == IntPtr.Zero)
                {
                    _imageBuffer = Marshal.AllocHGlobal(imageSize);
                }

                // 转换图像格式（根据实际像素格式调整）
                // 这里简化处理，假设已经是RGB格式
                // 实际使用时需要根据 pixelType 进行转换

                // 创建Bitmap
                Bitmap bitmap = new Bitmap(_imageWidth, _imageHeight, _imageWidth * 3, 
                    System.Drawing.Imaging.PixelFormat.Format24bppRgb, _imageBuffer);

                // 复制图像数据
                byte[] buffer = new byte[imageSize];
                Marshal.Copy(pData, buffer, 0, imageSize);
                Marshal.Copy(buffer, 0, _imageBuffer, imageSize);

                // 显示图像
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        BitmapImage bitmapImage = new BitmapImage();
                        using (MemoryStream ms = new MemoryStream())
                        {
                            bitmap.Save(ms, ImageFormat.Bmp);
                            ms.Position = 0;
                            bitmapImage.BeginInit();
                            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                            bitmapImage.StreamSource = ms;
                            bitmapImage.EndInit();
                            bitmapImage.Freeze();
                            CameraImage.Source = bitmapImage;
                        }
                    }
                    catch { }
                }));

                bitmap.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"显示图像异常: {ex.Message}");
            }
        }

        // 保存图像
        private void BtnSaveImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_isConnected)
                {
                    AddLog("请先连接相机");
                    return;
                }

                Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog();
                dialog.Filter = "BMP文件|*.bmp|JPEG文件|*.jpg|PNG文件|*.png";
                dialog.FileName = $"Image_{DateTime.Now:yyyyMMdd_HHmmss}";

                if (dialog.ShowDialog() == true)
                {
                    // 实际应用中应该保存原始图像数据
                    AddLog($"图像已保存到: {dialog.FileName}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"保存图像异常: {ex.Message}");
            }
        }

        // 添加日志
        private void AddLog(string message)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                string logEntry = $"[{DateTime.Now:HH:mm:ss}] {message}";
                LogTextBox.AppendText(logEntry + Environment.NewLine);
                LogTextBox.ScrollToEnd();
            }));
        }

        // 页面卸载时清理资源
        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            // 停止采集
            if (_isGrabbing)
            {
                StopGrabbing();
            }

            // 断开相机
            if (_isConnected && _device != null)
            {
                _device.Close();
                _device = null;
                _isConnected = false;
            }

            // 释放图像缓冲区
            if (_imageBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_imageBuffer);
                _imageBuffer = IntPtr.Zero;
            }
        }
    }
}
