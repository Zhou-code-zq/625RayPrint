using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using MvCamCtrl.NET;

namespace WpfApp1
{
    public partial class VisionInspectionPage : UserControl
    {
        // 海康SDK相关变量
        private MyCamera _camera = new MyCamera();
        private bool _isConnected = false;
        private bool _isGrabbing = false;
        private Thread _grabThread;
        private ManualResetEvent _stopEvent = new ManualResetEvent(false);
        
        // 图像相关
        private int _frameCount = 0;
        private DateTime _lastFrameTime = DateTime.Now;
        private double _currentFps = 0;
        
        // 相机参数
        private uint _cameraWidth = 1280;
        private uint _cameraHeight = 960;
        
        // 设备配置
        private int _deviceType = 0;  // 0=MV系列(GenTL), 1=CA系列(GigE), 2=CH系列(USB)
        private string _serialNo = "";
        private string _ipAddress = "";
        
        public VisionInspectionPage()
        {
            InitializeComponent();
        }
        
        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            AddLog("页面加载完成");
            UpdateButtonState();
        }
        
        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            DisconnectCamera();
        }
        
        /// <summary>
        /// 设置相机配置（由MainWindow调用）
        /// </summary>
        /// <param name="deviceType">设备类型：0=MV系列, 1=CA系列, 2=CH系列</param>
        /// <param name="serialNo">设备序列号</param>
        /// <param name="ipAddress">IP地址</param>
        public void SetCameraConfig(int deviceType, string serialNo, string ipAddress)
        {
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
                
                // 根据设备类型确定连接方式
                uint nDeviceType;
                string deviceTypeName;
                
                switch (_deviceType)
                {
                    case 0:  // MV系列 -> GenTL虚拟相机
                        nDeviceType = 6;  // GenTL
                        deviceTypeName = "GenTL 虚拟相机";
                        break;
                    case 1:  // CA系列 -> GigE工业相机
                        nDeviceType = MyCamera.MV_GIGE_DEVICE;
                        deviceTypeName = "GigE 工业相机";
                        break;
                    case 2:  // CH系列 -> USB相机
                        nDeviceType = MyCamera.MV_USB_DEVICE;
                        deviceTypeName = "USB 相机";
                        break;
                    default:
                        nDeviceType = 6;
                        deviceTypeName = "默认(GenTL)";
                        break;
                }
                
                AddLog($"设备类型: {deviceTypeName}");
                
                // 枚举设备
                MyCamera.MV_CC_DEVICE_INFO_LIST deviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
                int nRet = MyCamera.MV_CC_EnumDevices_NET(nDeviceType, ref deviceList);
                if (nRet != MyCamera.MV_OK)
                {
                    AddLog($"枚举设备失败，错误码: {nRet}");
                    MessageBox.Show($"枚举设备失败，错误码: {nRet}\n\n可能原因：\n1. 虚拟相机未在MVS中打开\n2. 设备未连接\n3. 驱动异常", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                if (deviceList.nDeviceNum == 0)
                {
                    AddLog("未发现设备！");
                    MessageBox.Show($"未发现 {deviceTypeName} 设备！\n\n请确保：\n1. 虚拟相机已在MVS中打开（如果是GenTL）\n2. 相机已连接（如果是物理相机）", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                AddLog($"发现 {deviceList.nDeviceNum} 个 {deviceTypeName} 设备");
                
                // 创建设备
                IntPtr pDeviceInfo = Marshal.UnsafeAddrOfPinnedArrayElement(deviceList.pDeviceInfo, 0);
                MyCamera.MV_CC_DEVICE_INFO deviceInfo = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));
                
                _camera = new MyCamera();
                nRet = _camera.MV_CC_CreateDevice_NET(ref deviceInfo);
                if (nRet != MyCamera.MV_OK)
                {
                    AddLog($"创建设备失败，错误码: {nRet}");
                    MessageBox.Show($"创建设备失败，错误码: {nRet}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                // 打开设备
                nRet = _camera.MV_CC_OpenDevice_NET(MyCamera.MV_ACCESS_Exclusive, 0);
                if (nRet != MyCamera.MV_OK)
                {
                    AddLog($"打开设备失败，错误码: {nRet}");
                    _camera.MV_CC_DestroyDevice_NET();
                    MessageBox.Show($"打开设备失败，错误码: {nRet}\n\n可能原因：\n1. 虚拟相机未在MVS中打开\n2. 设备被其他程序占用", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                _isConnected = true;
                AddLog("相机连接成功！");
                
                // 获取图像尺寸
                try
                {
                    MyCamera.MVCC_INTVALUE stWidth = new MyCamera.MVCC_INTVALUE();
                    nRet = _camera.MV_CC_GetIntValue_NET("Width", ref stWidth);
                    if (nRet == MyCamera.MV_OK)
                    {
                        _cameraWidth = stWidth.nCurValue;
                    }
                    
                    MyCamera.MVCC_INTVALUE stHeight = new MyCamera.MVCC_INTVALUE();
                    nRet = _camera.MV_CC_GetIntValue_NET("Height", ref stHeight);
                    if (nRet == MyCamera.MV_OK)
                    {
                        _cameraHeight = stHeight.nCurValue;
                    }
                    
                    AddLog($"相机分辨率: {_cameraWidth} x {_cameraHeight}");
                }
                catch (Exception ex)
                {
                    AddLog($"获取分辨率失败: {ex.Message}");
                }
                
                // 更新UI
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    ConnectionStatusText.Text = "状态: 已连接";
                    ConnectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(78, 205, 196));
                    CameraInfoText.Text = $"相机信息: {deviceTypeName}";
                    IpText.Text = $"分辨率: {_cameraWidth}x{_cameraHeight}";
                    UpdateButtonState();
                }));
            }
            catch (Exception ex)
            {
                AddLog($"连接异常: {ex.Message}");
                MessageBox.Show($"连接异常: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        // 开始采集
        private void BtnStartGrab_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                AddLog("相机未连接！");
                return;
            }
            
            try
            {
                int nRet = _camera.MV_CC_StartGrabbing_NET();
                if (nRet != MyCamera.MV_OK)
                {
                    AddLog($"开始采集失败，错误码: {nRet}");
                    return;
                }
                
                _isGrabbing = true;
                _stopEvent.Reset();
                _frameCount = 0;
                _lastFrameTime = DateTime.Now;
                
                _grabThread = new Thread(GrabThread);
                _grabThread.IsBackground = true;
                _grabThread.Start();
                
                AddLog("开始图像采集...");
                
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    PreviewPlaceholder.Visibility = Visibility.Collapsed;
                    CameraImage.Visibility = Visibility.Visible;
                    UpdateButtonState();
                }));
            }
            catch (Exception ex)
            {
                AddLog($"开始采集异常: {ex.Message}");
            }
        }
        
        // 采集线程
        private void GrabThread()
        {
            MyCamera.MV_FRAME_OUT frameOut = new MyCamera.MV_FRAME_OUT();
            int nRet;
            int consecutiveErrors = 0;
            
            while (!_stopEvent.WaitOne(10))
            {
                nRet = _camera.MV_CC_GetImageBuffer_NET(ref frameOut, 1000);
                if (nRet == MyCamera.MV_OK)
                {
                    consecutiveErrors = 0;
                    ProcessImage(frameOut);
                    _camera.MV_CC_FreeImageBuffer_NET(ref frameOut);
                }
                else
                {
                    consecutiveErrors++;
                    if (consecutiveErrors > 100)
                    {
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            AddLog("采集超时，停止线程");
                        }));
                        break;
                    }
                }
            }
        }
        
        // 处理图像
        private void ProcessImage(MyCamera.MV_FRAME_OUT frameOut)
        {
            try
            {
                _frameCount++;
                TimeSpan elapsed = DateTime.Now - _lastFrameTime;
                if (elapsed.TotalSeconds >= 1.0)
                {
                    _currentFps = _frameCount / elapsed.TotalSeconds;
                    _frameCount = 0;
                    _lastFrameTime = DateTime.Now;
                }
                
                int nWidth = (int)_cameraWidth;
                int nHeight = (int)_cameraHeight;
                
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        IntPtr pBufAddr = frameOut.pBufAddr;
                        
                        if (pBufAddr != IntPtr.Zero && nWidth > 0 && nHeight > 0)
                        {
                            BitmapSource bitmapSource = BitmapSource.Create(
                                nWidth,
                                nHeight,
                                96, 96,
                                PixelFormats.Bgr24,
                                null,
                                pBufAddr,
                                nWidth * nHeight * 3,
                                nWidth * 3);
                            
                            bitmapSource.Freeze();
                            CameraImage.Source = bitmapSource;
                        }
                    }
                    catch { }
                    
                    FpsText.Text = $"FPS: {_currentFps:F1}";
                }));
            }
            catch { }
        }
        
        // 停止采集
        private void BtnStopGrab_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _stopEvent.Set();
                
                if (_grabThread != null && _grabThread.IsAlive)
                {
                    _grabThread.Join(1000);
                }
                
                if (_camera != null)
                {
                    _camera.MV_CC_StopGrabbing_NET();
                }
                
                _isGrabbing = false;
                AddLog("停止图像采集");
                
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateButtonState();
                }));
            }
            catch (Exception ex)
            {
                AddLog($"停止采集异常: {ex.Message}");
            }
        }
        
        // 断开连接
        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            DisconnectCamera();
        }
        
        // 断开相机
        private void DisconnectCamera()
        {
            try
            {
                if (_isGrabbing)
                {
                    _stopEvent.Set();
                    if (_grabThread != null && _grabThread.IsAlive)
                    {
                        _grabThread.Join(1000);
                    }
                    
                    if (_camera != null)
                    {
                        _camera.MV_CC_StopGrabbing_NET();
                    }
                    _isGrabbing = false;
                }
                
                if (_camera != null && _isConnected)
                {
                    _camera.MV_CC_CloseDevice_NET();
                    _camera.MV_CC_DestroyDevice_NET();
                    _isConnected = false;
                    AddLog("相机已断开");
                }
                
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    ConnectionStatusText.Text = "状态: 未连接";
                    ConnectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 107, 107));
                    CameraInfoText.Text = "相机信息: --";
                    IpText.Text = "IP: --";
                    PreviewPlaceholder.Visibility = Visibility.Visible;
                    CameraImage.Visibility = Visibility.Collapsed;
                    CameraImage.Source = null;
                    UpdateButtonState();
                }));
            }
            catch (Exception ex)
            {
                AddLog($"断开连接异常: {ex.Message}");
            }
        }
        
        // 清空日志
        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogText.Text = "";
            AddLog("日志已清空");
        }
        
        // 添加日志
        private void AddLog(string message)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                string time = DateTime.Now.ToString("HH:mm:ss");
                LogText.Text += $"[{time}] {message}\n";
                LogScrollViewer.ScrollToEnd();
            }));
        }
        
        // 更新按钮状态
        private void UpdateButtonState()
        {
            BtnConnect.IsEnabled = !_isConnected;
            BtnStartGrab.IsEnabled = _isConnected && !_isGrabbing;
            BtnStopGrab.IsEnabled = _isGrabbing;
            BtnDisconnect.IsEnabled = _isConnected;
        }
        
        // 提供公共方法供外部调用设置相机信息（保留兼容）
        public void SetCameraInfo(string serial, string ip)
        {
            _serialNo = serial;
            _ipAddress = ip;
        }
    }
}
