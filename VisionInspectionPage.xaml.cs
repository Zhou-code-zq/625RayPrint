using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.IO;
using System.Text;

namespace WpfApp1
{
    public partial class VisionInspectionPage : UserControl
    {
        // 海康SDK托管API
        private MvCamCtrl.NET.Camera _camera = null;
        
        // 变量
        private bool _isConnected = false;
        private bool _isGrabbing = false;
        private Thread _grabThread = null;
        private ManualResetEvent _stopEvent = new ManualResetEvent(false);
        private WriteableBitmap _writeableBmp = null;
        private int _frameCount = 0;
        private DateTime _startTime;
        private string _cameraIP = "";
        private string _cameraSerial = "";
        private bool _sdkInitialized = false;

        public VisionInspectionPage()
        {
            InitializeComponent();
            InitializeSDK();
        }

        // 初始化SDK
        private void InitializeSDK()
        {
            try
            {
                // 尝试初始化海康SDK
                _sdkInitialized = true;
                AddLog("海康SDK已就绪");
            }
            catch (Exception ex)
            {
                AddLog("SDK初始化失败: " + ex.Message);
                _sdkInitialized = false;
            }
        }

        // 页面加载事件
        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            AddLog("质检与监控页面已加载");
            if (!_sdkInitialized)
            {
                AddLog("警告: SDK未初始化，请在控制台查看详细错误");
            }
        }

        // 连接相机
        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            AddLog("正在连接相机...");
            
            try
            {
                if (_camera != null)
                {
                    _camera.Dispose();
                    _camera = null;
                }

                // 创建设备列表
                var deviceList = new MvCamCtrl.NET.DeviceList();
                int nRet = MvCamCtrl.NET.Camera.EnumDevices(deviceList, MvCamCtrl.NET.MvGvspConfig.TLType.GigE);
                
                if (nRet != 0 || deviceList.Count == 0)
                {
                    AddLog("未发现设备，设备数量: " + deviceList.Count);
                    MessageBox.Show("未发现海康相机设备！\n请检查：\n1. 相机是否已连接\n2. 相机IP地址是否正确\n3. 网线是否连接正常", "连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AddLog("发现 " + deviceList.Count + " 个设备");

                // 创建相机实例
                _camera = new MvCamCtrl.NET.Camera();
                
                // 获取第一个设备信息
                var deviceInfo = deviceList[0];
                string serialNo = deviceInfo.GetSerialNumber();
                string model = deviceInfo.GetModelName();
                
                UpdateCameraInfo(model, serialNo);
                _cameraSerial = serialNo;
                
                // 打开设备
                nRet = _camera.Open(MvCamCtrl.NET.MvAccessMode.Exclusive, 0);
                if (nRet != 0)
                {
                    AddLog("打开设备失败，错误码: " + nRet);
                    MessageBox.Show("打开相机失败！\n错误码: " + nRet, "连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                _isConnected = true;
                _cameraIP = deviceInfo.GetIpAddress();
                AddLog("相机连接成功！IP: " + _cameraIP);
                UpdateConnectionStatus("状态: 已连接", Color.FromRgb(78, 205, 196));
                UpdateButtonState(false, true, false);
            }
            catch (Exception ex)
            {
                AddLog("连接异常: " + ex.Message);
                MessageBox.Show("连接相机失败！\n\n详细信息: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 开始采集
        private void BtnStartGrab_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected || _camera == null)
            {
                AddLog("请先连接相机！");
                return;
            }

            try
            {
                // 设置图像格式
                _camera.SetPixelFormat(MvCamCtrl.NET.MvPixelFormat.PixelFormat_Gvsp_Mono8);
                
                // 开始采集
                int nRet = _camera.StartGrabbing();
                if (nRet != 0)
                {
                    AddLog("开始采集失败，错误码: " + nRet);
                    MessageBox.Show("开始采集失败！\n错误码: " + nRet, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                _isGrabbing = true;
                _startTime = DateTime.Now;
                _frameCount = 0;
                _stopEvent.Reset();

                // 获取相机参数用于创建Bitmap
                uint width = _camera.GetWidth();
                uint height = _camera.GetHeight();
                if (width == 0) width = 1280;
                if (height == 0) height = 720;

                // 创建图像显示
                Dispatcher.Invoke(new Action(() =>
                {
                    _writeableBmp = new WriteableBitmap((int)width, (int)height, 96, 96, PixelFormats.Bgra32, null);
                    CameraImage.Source = _writeableBmp;
                }));

                // 启动采集线程
                _grabThread = new Thread(GrabThread);
                _grabThread.IsBackground = true;
                _grabThread.Start(width, height);

                AddLog("开始采集...");
                UpdateButtonState(false, false, true);
            }
            catch (Exception ex)
            {
                AddLog("开始采集异常: " + ex.Message);
                MessageBox.Show("开始采集失败！\n\n详细信息: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void GrabThread(object param)
        {
            uint width = (uint)((object[])param)[0];
            uint height = (uint)((object[])param)[1];
            var frameOut = new MvCamCtrl.NET.FrameOut();
            int fps = 0;
            DateTime lastFpsTime = DateTime.Now;

            while (_isGrabbing && !_stopEvent.WaitOne(10))
            {
                try
                {
                    if (_camera != null && _isGrabbing)
                    {
                        int nRet = _camera.GetImageBuffer(ref frameOut, 1000);
                        if (nRet == 0 && frameOut.pBufAddr != IntPtr.Zero)
                        {
                            _frameCount++;
                            
                            // 计算FPS
                            fps++;
                            var now = DateTime.Now;
                            if ((now - lastFpsTime).TotalSeconds >= 1)
                            {
                                Dispatcher.Invoke(new Action(() =>
                                {
                                    UpdateFrameCount(fps);
                                }));
                                fps = 0;
                                lastFpsTime = now;
                            }

                            // 显示图像
                            if (_writeableBmp != null)
                            {
                                Dispatcher.Invoke(new Action(() =>
                                {
                                    try
                                    {
                                        IntPtr srcAddr = frameOut.pBufAddr;
                                        IntPtr destAddr = _writeableBmp.BackBuffer;
                                        int byteWidth = (int)width * 4;
                                        
                                        for (int i = 0; i < (int)height; i++)
                                        {
                                            IntPtr srcRow = srcAddr + i * (int)width;
                                            IntPtr destRow = destAddr + i * byteWidth;
                                            MvCamCtrl.NET.MvCamCtrl.CopyMemory(destRow, srcRow, (uint)byteWidth);
                                        }
                                        
                                        _writeableBmp.Lock();
                                        _writeableBmp.AddDirtyRect(new Int32Rect(0, 0, (int)width, (int)height));
                                        _writeableBmp.Unlock();
                                    }
                                    catch { }
                                }));
                            }

                            // 释放缓存
                            _camera.FreeImageBuffer(ref frameOut);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(new Action(() =>
                    {
                        AddLog("采集异常: " + ex.Message);
                    }));
                }
            }
        }

        // 停止采集
        private void BtnStopGrab_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _isGrabbing = false;
                _stopEvent.Set();

                if (_camera != null)
                {
                    _camera.StopGrabbing();
                    AddLog("已停止采集");
                }

                UpdateButtonState(true, false, false);
            }
            catch (Exception ex)
            {
                AddLog("停止采集异常: " + ex.Message);
            }
        }

        // 断开连接
        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _isGrabbing = false;
                _stopEvent.Set();

                if (_grabThread != null && _grabThread.IsAlive)
                {
                    _grabThread.Join(1000);
                }

                if (_camera != null)
                {
                    _camera.StopGrabbing();
                    _camera.Close();
                    _camera.Dispose();
                    _camera = null;
                }

                _isConnected = false;
                AddLog("已断开相机连接");
                UpdateConnectionStatus("状态: 未连接", Color.FromRgb(158, 158, 158));
                UpdateCameraInfo("-", "-");
                UpdateButtonState(true, false, false);
                
                // 清空图像
                Dispatcher.Invoke(new Action(() =>
                {
                    CameraImage.Source = null;
                }));
            }
            catch (Exception ex)
            {
                AddLog("断开连接异常: " + ex.Message);
            }
        }

        // 添加日志
        private void AddLog(string message)
        {
            try
            {
                string time = DateTime.Now.ToString("HH:mm:ss");
                string logMessage = "[" + time + "] " + message;
                
                Dispatcher.Invoke(new Action(() =>
                {
                    LogText.Text += logMessage + "\n";
                    LogText.ScrollToEnd();
                }));
            }
            catch { }
        }

        // 更新连接状态
        private void UpdateConnectionStatus(string status, Color color)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                ConnectionStatusText.Text = status;
                ConnectionStatusText.Foreground = new SolidColorBrush(color);
            }));
        }

        // 更新相机信息
        private void UpdateCameraInfo(string model, string serial)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                CameraInfoText.Text = "型号: " + model + "\n序列号: " + serial;
            }));
        }

        // 更新帧数
        private void UpdateFrameCount(int fps = 0)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                FrameCountText.Text = "帧数: " + _frameCount + "  FPS: " + fps;
            }));
        }

        // 更新按钮状态
        private void UpdateButtonState(bool connectEnabled, bool startEnabled, bool stopEnabled)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                BtnConnect.IsEnabled = connectEnabled;
                BtnStartGrab.IsEnabled = startEnabled;
                BtnStopGrab.IsEnabled = stopEnabled;
                BtnDisconnect.IsEnabled = _isConnected;
            }));
        }

        // 页面卸载
        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _isGrabbing = false;
                _stopEvent.Set();

                if (_grabThread != null && _grabThread.IsAlive)
                {
                    _grabThread.Join(1000);
                }

                if (_camera != null)
                {
                    try { _camera.StopGrabbing(); } catch { }
                    try { _camera.Close(); } catch { }
                    _camera.Dispose();
                    _camera = null;
                }
            }
            catch { }
        }

        // 提供公共方法供MainWindow调用
        public void SetCameraInfo(string ip, string serial)
        {
            _cameraIP = ip;
            _cameraSerial = serial;
        }
    }
}
