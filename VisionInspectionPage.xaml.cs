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
        
        // 设备列表
        private MyCamera.MV_CC_DEVICE_INFO_LIST _deviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
        private MyCamera.MV_CC_DEVICE_INFO_LIST _genTlList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
        private MyCamera.MV_CC_DEVICE_INFO_LIST _gigeList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
        private MyCamera.MV_CC_DEVICE_INFO_LIST _usbList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
        
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
        
        // 连接相机按钮
        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (CameraSelectComboBox.SelectedIndex < 0)
            {
                // 第一次点击 - 枚举设备
                EnumDevices();
            }
            else
            {
                // 第二次点击 - 连接选中的相机
                ConnectSelectedCamera();
            }
        }
        
        // 枚举设备
        private void EnumDevices()
        {
            try
            {
                AddLog("正在枚举设备...");
                
                // 清空下拉框
                CameraSelectComboBox.Items.Clear();
                _deviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
                _genTlList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
                _gigeList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
                _usbList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
                
                int totalDevices = 0;
                
                // GenTL设备（虚拟相机）
                int nRet = MyCamera.MV_CC_EnumDevices_NET(6, ref _genTlList);
                if (nRet == MyCamera.MV_OK)
                {
                    for (int i = 0; i < _genTlList.nDeviceNum; i++)
                    {
                        CameraSelectComboBox.Items.Add("[GenTL] 虚拟相机 #" + (i + 1));
                        totalDevices++;
                    }
                    if (_genTlList.nDeviceNum > 0)
                        AddLog("GenTL 虚拟相机: " + _genTlList.nDeviceNum);
                }
                
                // GigE设备
                nRet = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE, ref _gigeList);
                if (nRet == MyCamera.MV_OK)
                {
                    for (int i = 0; i < _gigeList.nDeviceNum; i++)
                    {
                        CameraSelectComboBox.Items.Add("[GigE] 工业相机 #" + (i + 1));
                        totalDevices++;
                    }
                    if (_gigeList.nDeviceNum > 0)
                        AddLog("GigE 工业相机: " + _gigeList.nDeviceNum);
                }
                
                // USB设备
                nRet = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_USB_DEVICE, ref _usbList);
                if (nRet == MyCamera.MV_OK)
                {
                    for (int i = 0; i < _usbList.nDeviceNum; i++)
                    {
                        CameraSelectComboBox.Items.Add("[USB] 相机 #" + (i + 1));
                        totalDevices++;
                    }
                    if (_usbList.nDeviceNum > 0)
                        AddLog("USB 相机: " + _usbList.nDeviceNum);
                }
                
                if (totalDevices == 0)
                {
                    AddLog("未发现任何相机设备！");
                    MessageBox.Show("未发现任何相机设备。\n\n请确保：\n1. 已安装MVS并正确配置\n2. 已添加虚拟相机（在MVS菜单: 工具->虚拟相机)\n3. 虚拟相机已在MVS中打开", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                // 选中第一个设备
                CameraSelectComboBox.SelectedIndex = 0;
                AddLog("发现 " + totalDevices + " 个设备，请从列表中选择");
                
                // 更新按钮文字
                BtnConnect.Content = "连接所选相机";
            }
            catch (Exception ex)
            {
                AddLog("枚举设备异常: " + ex.Message);
                MessageBox.Show("枚举设备异常: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        // 连接选中的相机
        private void ConnectSelectedCamera()
        {
            try
            {
                int selectedIndex = CameraSelectComboBox.SelectedIndex;
                if (selectedIndex < 0)
                {
                    MessageBox.Show("请先点击\"连接相机\"按钮发现设备！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                AddLog("正在连接: " + CameraSelectComboBox.SelectedItem);
                
                // 确定设备所在的列表和索引
                MyCamera.MV_CC_DEVICE_INFO deviceInfo = new MyCamera.MV_CC_DEVICE_INFO();
                IntPtr pDeviceInfo;
                
                // 计算选中的设备属于哪个列表
                int genTlCount = (int)_genTlList.nDeviceNum;
                int gigeCount = (int)_gigeList.nDeviceNum;
                
                if (selectedIndex < genTlCount)
                {
                    // GenTL设备
                    pDeviceInfo = Marshal.UnsafeAddrOfPinnedArrayElement(_genTlList.pDeviceInfo, selectedIndex);
                    deviceInfo = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));
                    _deviceList = _genTlList;
                    AddLog("连接 GenTL 虚拟相机");
                }
                else if (selectedIndex < genTlCount + gigeCount)
                {
                    // GigE设备
                    int gigeIndex = selectedIndex - genTlCount;
                    pDeviceInfo = Marshal.UnsafeAddrOfPinnedArrayElement(_gigeList.pDeviceInfo, gigeIndex);
                    deviceInfo = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));
                    _deviceList = _gigeList;
                    AddLog("连接 GigE 工业相机");
                }
                else
                {
                    // USB设备
                    int usbIndex = selectedIndex - genTlCount - gigeCount;
                    pDeviceInfo = Marshal.UnsafeAddrOfPinnedArrayElement(_usbList.pDeviceInfo, usbIndex);
                    deviceInfo = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));
                    _deviceList = _usbList;
                    AddLog("连接 USB 相机");
                }
                
                // 创建相机实例
                _camera = new MyCamera();
                int nRet = _camera.MV_CC_CreateDevice_NET(ref deviceInfo);
                if (nRet != MyCamera.MV_OK)
                {
                    AddLog("创建设备失败，错误码: " + nRet);
                    MessageBox.Show("创建设备失败，错误码: " + nRet + "\n\n可能原因：\n1. 虚拟相机未在MVS中打开\n2. 设备被其他程序占用\n3. 驱动异常", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                // 打开设备
                nRet = _camera.MV_CC_OpenDevice_NET(MyCamera.MV_ACCESS_Exclusive, 0);
                if (nRet != MyCamera.MV_OK)
                {
                    AddLog("打开设备失败，错误码: " + nRet);
                    _camera.MV_CC_DestroyDevice_NET();
                    MessageBox.Show("打开设备失败，错误码: " + nRet + "\n\n可能原因：\n1. 虚拟相机未在MVS中打开\n2. 设备被其他程序占用", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    
                    AddLog("相机分辨率: " + _cameraWidth + " x " + _cameraHeight);
                }
                catch (Exception ex)
                {
                    AddLog("获取分辨率失败: " + ex.Message);
                }
                
                // 更新UI
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    ConnectionStatusText.Text = "状态: 已连接";
                    ConnectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(78, 205, 196));
                    CameraInfoText.Text = "相机信息: " + CameraSelectComboBox.SelectedItem;
                    IpText.Text = "分辨率: " + _cameraWidth + "x" + _cameraHeight;
                    CameraSelectComboBox.IsEnabled = false;
                    BtnConnect.Content = "已连接";
                    BtnConnect.IsEnabled = false;
                    UpdateButtonState();
                }));
            }
            catch (Exception ex)
            {
                AddLog("连接异常: " + ex.Message);
                MessageBox.Show("连接异常: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    AddLog("开始采集失败，错误码: " + nRet);
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
                AddLog("开始采集异常: " + ex.Message);
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
                    
                    FpsText.Text = "FPS: " + _currentFps.ToString("F1");
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
                AddLog("停止采集异常: " + ex.Message);
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
                    CameraSelectComboBox.IsEnabled = true;
                    CameraSelectComboBox.Items.Clear();
                    CameraSelectComboBox.SelectedIndex = -1;
                    BtnConnect.Content = "连接相机";
                    BtnConnect.IsEnabled = true;
                    UpdateButtonState();
                }));
            }
            catch (Exception ex)
            {
                AddLog("断开连接异常: " + ex.Message);
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
                LogText.Text += "[" + time + "] " + message + "\n";
                LogScrollViewer.ScrollToEnd();
            }));
        }
        
        // 更新按钮状态
        private void UpdateButtonState()
        {
            BtnStartGrab.IsEnabled = _isConnected && !_isGrabbing;
            BtnStopGrab.IsEnabled = _isGrabbing;
            BtnDisconnect.IsEnabled = _isConnected;
        }
        
        // 提供公共方法供外部调用设置相机信息
        public void SetCameraInfo(string serial, string ip)
        {
        }
    }
}
