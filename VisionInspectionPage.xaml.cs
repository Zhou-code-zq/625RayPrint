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
        private MyCamera.MV_CC_DEVICE_INFO_LIST _deviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
        
        // 图像相关
        private WriteableBitmap _bitmap;
        private int _frameCount = 0;
        private DateTime _lastFrameTime = DateTime.Now;
        private double _currentFps = 0;
        
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
        
        // 连接相机
        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AddLog("正在枚举设备...");
                
                // 枚举设备 - GigE和USB
                int nRet = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, ref _deviceList);
                if (nRet != MyCamera.MV_OK)
                {
                    AddLog("枚举设备失败，错误码: " + nRet);
                    return;
                }
                
                if (_deviceList.nDeviceNum == 0)
                {
                    AddLog("未发现任何相机设备！");
                    MessageBox.Show("未发现任何相机设备，请检查相机连接。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                string deviceInfo = "发现 " + _deviceList.nDeviceNum + " 个设备";
                AddLog(deviceInfo);
                
                // 创建设备并打开
                nRet = _camera.MV_CC_CreateDevice_NET(ref _deviceList);
                if (nRet != MyCamera.MV_OK)
                {
                    AddLog("创建设备失败，错误码: " + nRet);
                    return;
                }
                
                // 打开设备
                nRet = _camera.MV_CC_OpenDevice_NET(MyCamera.MV_ACCESS_Exclusive, 0);
                if (nRet != MyCamera.MV_OK)
                {
                    AddLog("打开设备失败，错误码: " + nRet);
                    _camera.MV_CC_DestroyDevice_NET();
                    MessageBox.Show("打开设备失败，错误码: " + nRet, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                _isConnected = true;
                AddLog("相机连接成功！");
                
                // 更新UI
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    ConnectionStatusText.Text = "状态: 已连接";
                    ConnectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(78, 205, 196));
                    CameraInfoText.Text = "相机信息: 已连接";
                    IpText.Text = "设备数: " + _deviceList.nDeviceNum;
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
                // 开始采集
                int nRet = _camera.MV_CC_StartGrabbing_NET();
                if (nRet != MyCamera.MV_OK)
                {
                    AddLog("开始采集失败，错误码: " + nRet);
                    return;
                }
                
                _isGrabbing = true;
                _stopEvent.Reset();
                
                // 启动采集线程
                _grabThread = new Thread(GrabThread);
                _grabThread.IsBackground = true;
                _grabThread.Start();
                
                AddLog("开始图像采集...");
                
                // 更新UI
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
            
            while (!_stopEvent.WaitOne(10))
            {
                nRet = _camera.MV_CC_GetImageBuffer_NET(ref frameOut, 1000);
                if (nRet == MyCamera.MV_OK)
                {
                    // 处理图像数据
                    ProcessImage(frameOut);
                    
                    // 释放缓存
                    _camera.MV_CC_FreeImageBuffer_NET(ref frameOut);
                }
            }
        }
        
        // 处理图像
        private void ProcessImage(MyCamera.MV_FRAME_OUT frameOut)
        {
            try
            {
                // 更新帧率
                _frameCount++;
                TimeSpan elapsed = DateTime.Now - _lastFrameTime;
                if (elapsed.TotalSeconds >= 1.0)
                {
                    _currentFps = _frameCount / elapsed.TotalSeconds;
                    _frameCount = 0;
                    _lastFrameTime = DateTime.Now;
                }
                
                // 在UI线程更新显示
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    // 获取图像参数
                    int nWidth = (int)frameOut.nWidth;
                    int nHeight = (int)frameOut.nHeight;
                    IntPtr pBufAddr = frameOut.pBufAddr;
                    
                    if (nWidth > 0 && nHeight > 0 && pBufAddr != IntPtr.Zero)
                    {
                        // 创建BitmapSource
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
                    
                    // 更新统计
                    FrameCountText.Text = "帧数: " + _frameCount;
                    FpsText.Text = "帧率: " + _currentFps.ToString("F1") + " FPS";
                    FpsText.Text = "FPS: " + _currentFps.ToString("F1");
                }));
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    AddLog("处理图像异常: " + ex.Message);
                }));
            }
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
            BtnConnect.IsEnabled = !_isConnected;
            BtnStartGrab.IsEnabled = _isConnected && !_isGrabbing;
            BtnStopGrab.IsEnabled = _isGrabbing;
            BtnDisconnect.IsEnabled = _isConnected;
        }
        
        // 提供公共方法供外部调用设置相机信息
        public void SetCameraInfo(string serial, string ip)
        {
            // 可以在这里存储相机信息
        }
    }
}
