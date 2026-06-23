using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

using MvCamCtrl.NET;

namespace WpfApp1
{
    public partial class VisionInspectionPage : Page
    {
        // 相机对象
        private MyCamera _camera = new MyCamera();
        private bool _isConnected = false;
        private bool _isGrabbing = false;
        private Thread _grabThread = null;

        // 相机参数
        private string _serialNo = "";
        private string _ipAddress = "";
        private int _deviceType = 0;  // 0=MV系列(GenTL), 1=CA系列(GigE), 2=CH系列(USB)

        public VisionInspectionPage()
        {
            InitializeComponent();
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
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
                uint deviceTLayer;
                string deviceTypeName;
                
                switch (_deviceType)
                {
                    case 0:  // MV系列 -> GenTL虚拟相机
                        deviceTLayer = MyCamera.MV_GENTL_DEVICE;
                        deviceTypeName = "GenTL 虚拟相机";
                        break;
                    case 1:  // CA系列 -> GigE工业相机
                        deviceTLayer = MyCamera.MV_GIGE_DEVICE;
                        deviceTypeName = "GigE 工业相机";
                        break;
                    case 2:  // CH系列 -> USB相机
                        deviceTLayer = MyCamera.MV_USB_DEVICE;
                        deviceTypeName = "USB 相机";
                        break;
                    default:
                        deviceTLayer = MyCamera.MV_GENTL_DEVICE;
                        deviceTypeName = "默认(GenTL)";
                        break;
                }
                
                AddLog($"设备类型: {deviceTypeName}");

                // 创建设备列表
                MyCamera.MV_CC_DEVICE_INFO_LIST deviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
                
                // 枚举设备
                int nRet = _camera.MV_CC_EnumDevices_NET(deviceTLayer, ref deviceList);
                if (nRet != 0)
                {
                    AddLog($"枚举设备失败，错误码: {nRet}");
                    return;
                }

                if (deviceList.nDeviceNum == 0)
                {
                    AddLog($"未发现 {deviceTypeName} 设备");
                    return;
                }

                AddLog($"发现 {deviceList.nDeviceNum} 个 {deviceTypeName} 设备");

                // 根据序列号或IP地址查找目标设备
                int targetIndex = -1;
                for (int i = 0; i < deviceList.nDeviceNum; i++)
                {
                    IntPtr pDeviceInfo = Marshal.UnsafeAddrOfPinnedArrayElement(deviceList.pDeviceInfo, i);
                    MyCamera.MV_CC_DEVICE_INFO deviceInfo = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));
                    
                    // 检查是否匹配
                    bool match = false;
                    
                    // 尝试获取设备信息（不同类型结构不同）
                    string serial = GetDeviceSerial(deviceInfo);
                    string ip = GetDeviceIp(deviceInfo);
                    
                    if (!string.IsNullOrEmpty(_serialNo) && !string.IsNullOrEmpty(serial) && serial.Contains(_serialNo))
                    {
                        match = true;
                    }
                    else if (!string.IsNullOrEmpty(_ipAddress) && !string.IsNullOrEmpty(ip) && ip.Contains(_ipAddress))
                    {
                        match = true;
                    }
                    
                    if (match)
                    {
                        targetIndex = i;
                        AddLog($"找到匹配的设备");
                        break;
                    }
                }

                // 如果没找到指定设备，使用第一个
                if (targetIndex < 0)
                {
                    targetIndex = 0;
                    AddLog($"使用第一个可用设备");
                }

                // 创建相机
                IntPtr pDeviceInfo2 = Marshal.UnsafeAddrOfPinnedArrayElement(deviceList.pDeviceInfo, targetIndex);
                MyCamera.MV_CC_DEVICE_INFO device = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo2, typeof(MyCamera.MV_CC_DEVICE_INFO));
                
                nRet = _camera.MV_CC_CreateDevice_NET(ref device);
                if (nRet != 0)
                {
                    AddLog($"创建设备失败，错误码: {nRet}");
                    return;
                }

                // 打开设备
                nRet = _camera.MV_CC_OpenDevice_NET(MyCamera.MV_ACCESS_Exclusive, 0);
                if (nRet != 0)
                {
                    AddLog($"打开设备失败，错误码: {nRet}");
                    _camera.MV_CC_DestroyDevice_NET();
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

        // 获取设备序列号
        private string GetDeviceSerial(MyCamera.MV_CC_DEVICE_INFO deviceInfo)
        {
            try
            {
                // 尝试从不同结构获取序列号
                return System.Text.Encoding.ASCII.GetString(deviceInfo.SpecialInfo.stGigEInfo.chSerialNumber).TrimEnd('\0');
            }
            catch
            {
                try
                {
                    return System.Text.Encoding.ASCII.GetString(deviceInfo.SpecialInfo.stUsb3VInfo.chSerialNumber).TrimEnd('\0');
                }
                catch
                {
                    try
                    {
                        return System.Text.Encoding.ASCII.GetString(deviceInfo.SpecialInfo.stCamLinkInfo.chSerialNumber).TrimEnd('\0');
                    }
                    catch
                    {
                        return "";
                    }
                }
            }
        }

        // 获取设备IP地址
        private string GetDeviceIp(MyCamera.MV_CC_DEVICE_INFO deviceInfo)
        {
            try
            {
                // 获取GigE设备IP
                uint nIp1 = deviceInfo.SpecialInfo.stGigEInfo.nCurrentIp;
                return $"{nIp1 & 0xFF}.{(nIp1 >> 8) & 0xFF}.{(nIp1 >> 16) & 0xFF}.{(nIp1 >> 24) & 0xFF}";
            }
            catch
            {
                return "";
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

                if (_camera != null)
                {
                    _camera.MV_CC_CloseDevice_NET();
                    _camera.MV_CC_DestroyDevice_NET();
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
                // 开始采集
                int nRet = _camera.MV_CC_StartGrabbing_NET();
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

                if (_camera != null)
                {
                    _camera.MV_CC_StopGrabbing_NET();
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
            int nWidth = 2448;
            int nHeight = 2048;
            IntPtr pData = IntPtr.Zero;
            MyCamera.MV_FRAME_OUT frameOut = new MyCamera.MV_FRAME_OUT();

            while (_isGrabbing && _isConnected)
            {
                try
                {
                    // 获取一帧图像
                    int nRet = _camera.MV_CC_GetImageBuffer_NET(ref frameOut, 1000);
                    
                    if (nRet == 0 && frameOut.pBufAddr != IntPtr.Zero)
                    {
                        pData = frameOut.pBufAddr;
                        nWidth = (int)frameOut.nWidth;
                        nHeight = (int)frameOut.nHeight;

                        // 更新帧计数
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                FrameCountText.Text = (int.Parse(FrameCountText.Text) + 1).ToString();
                            }
                            catch { }
                        }));

                        // 显示图像
                        ShowImage(pData, nWidth, nHeight);

                        // 释放图像缓存
                        _camera.MV_CC_FreeImageBuffer_NET(ref frameOut);
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
                if (pData == IntPtr.Zero || nWidth <= 0 || nHeight <= 0)
                    return;

                // 计算图像大小（假设RGB8格式，实际根据相机像素格式调整）
                int stride = nWidth * 3;
                byte[] rgbData = new byte[nWidth * nHeight * 3];
                
                // 复制图像数据
                Marshal.Copy(pData, rgbData, 0, rgbData.Length);

                // 创建Bitmap
                using (Bitmap bitmap = new Bitmap(nWidth, nHeight, stride, System.Drawing.Imaging.PixelFormat.Format24bppRgb, pData))
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            using (MemoryStream ms = new MemoryStream())
                            {
                                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                                ms.Position = 0;
                                
                                BitmapImage bitmapImage = new BitmapImage();
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
                }
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
            if (_isConnected && _camera != null)
            {
                _camera.MV_CC_CloseDevice_NET();
                _camera.MV_CC_DestroyDevice_NET();
                _isConnected = false;
            }
        }
    }
}
