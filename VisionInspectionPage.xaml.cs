using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;

using MvCamCtrl.NET;

namespace WpfApp1
{
    public partial class VisionInspectionPage : UserControl
    {
        // 相机对象
        private MyCamera _camera = new MyCamera();
        private bool _isConnected = false;
        private bool _isGrabbing = false;
        private Thread _grabThread = null;

        // 相机参数
        private string _serialNo = "";

        public VisionInspectionPage()
        {
            InitializeComponent();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            LoadCameraInfo();
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        // 从参数配置页面加载相机信息
        private void LoadCameraInfo()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // 直接访问参数配置页面的控件
                var configPage = MainWindow.GetConfigPage();
                if (configPage != null)
                {
                    _serialNo = configPage.SerialNo;
                    AddLog($"已加载配置：序列号={_serialNo}");
                }
            });
        }

        // 添加日志
        private void AddLog(string message)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                LogText.Text += $"[{timestamp}] {message}\r\n";
                LogScrollViewer.ScrollToEnd();
            });
        }

        // 更新UI状态
        private void UpdateUI(bool connected)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                BtnConnect.IsEnabled = true;
                BtnStartGrab.IsEnabled = connected;
                BtnStopGrab.IsEnabled = connected && _isGrabbing;
                BtnDisconnect.IsEnabled = connected;
                ConnectionStatusText.Text = connected ? "状态: 已连接" : "状态: 未连接";
                ConnectionStatusText.Foreground = connected ? 
                    new SolidColorBrush(Color.FromRgb(78, 205, 196)) : 
                    new SolidColorBrush(Color.FromRgb(255, 107, 107));
                
                if (connected)
                {
                    PreviewPlaceholder.Visibility = Visibility.Collapsed;
                    CameraImage.Visibility = Visibility.Visible;
                }
                else
                {
                    PreviewPlaceholder.Visibility = Visibility.Visible;
                    CameraImage.Visibility = Visibility.Collapsed;
                }
            });
        }

        // 连接相机按钮
        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected)
            {
                DisconnectCamera();
                return;
            }
            
            if (string.IsNullOrEmpty(_serialNo))
            {
                MessageBox.Show("请先在\"参数配置\"页面设置相机序列号并保存！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            ConnectCameraBySerialNo(_serialNo);
        }

        // 通过序列号连接相机
        private void ConnectCameraBySerialNo(string serialNo)
        {
            try
            {
                AddLog($"正在查找序列号为 {serialNo} 的相机...");

                // 设备类型数组 (包含 GenTL 虚拟相机)
                uint[] deviceTypes = new uint[] { 
                    MyCamera.MV_GIGE_DEVICE,      // GigE
                    MyCamera.MV_USB_DEVICE,       // USB
                    0x00000004u                   // GenTL 虚拟相机
                };

                MyCamera.MV_CC_DEVICE_INFO targetDevice = new MyCamera.MV_CC_DEVICE_INFO();
                bool found = false;
                string foundType = "";

                // 遍历所有设备类型查找匹配的序列号
                foreach (uint deviceType in deviceTypes)
                {
                    MyCamera.MV_CC_DEVICE_INFO_LIST deviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
                    int enumRet = MyCamera.MV_CC_EnumDevices_NET(deviceType, ref deviceList);
                    
                    if (enumRet != 0)
                        continue;

                    string typeName = deviceType == MyCamera.MV_GIGE_DEVICE ? "GigE" : 
                                      deviceType == MyCamera.MV_USB_DEVICE ? "USB" : "GenTL";
                    AddLog($"检查 {typeName} 设备: 发现 {deviceList.nDeviceNum} 个");

                    for (int i = 0; i < deviceList.nDeviceNum; i++)
                    {
                        IntPtr pInfo = Marshal.UnsafeAddrOfPinnedArrayElement(deviceList.pDeviceInfo, i);
                        MyCamera.MV_CC_DEVICE_INFO deviceInfo = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(
                            pInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));

                        // 获取序列号 - 使用固定的字节偏移量
                        string deviceSN = GetDeviceSerialNoFromPtr(pInfo, deviceType);
                        
                        AddLog($"  [{i}] 序列号: {deviceSN}");

                        // 序列号匹配
                        if (!string.IsNullOrEmpty(deviceSN) && deviceSN.Trim() == serialNo.Trim())
                        {
                            targetDevice = deviceInfo;
                            found = true;
                            foundType = typeName;
                            AddLog($"找到匹配的 {typeName} 相机！");
                            break;
                        }
                    }

                    if (found)
                        break;
                }

                if (!found)
                {
                    AddLog($"未找到序列号为 {serialNo} 的相机");
                    return;
                }

                // 创建相机
                AddLog("正在创建设备...");
                int createRet = _camera.MV_CC_CreateDevice_NET(ref targetDevice);
                if (createRet != 0)
                {
                    AddLog($"创建设备失败，错误码: {createRet}");
                    return;
                }

                // 打开设备
                AddLog("正在打开设备...");
                int openRet = _camera.MV_CC_OpenDevice_NET(MyCamera.MV_ACCESS_Exclusive, 0);
                if (openRet != 0)
                {
                    AddLog($"打开设备失败，错误码: {openRet}");
                    _camera.MV_CC_DestroyDevice_NET();
                    return;
                }

                _isConnected = true;
                AddLog("相机连接成功！");
                UpdateUI(true);
            }
            catch (Exception ex)
            {
                AddLog($"连接异常: {ex.Message}");
            }
        }

        // 从IntPtr获取设备序列号
        private string GetDeviceSerialNoFromPtr(IntPtr pInfo, uint deviceType)
        {
            try
            {
                // 使用固定偏移量读取序列号
                // MV_CC_DEVICE_INFO 结构中序列号的位置大约在 96-128 字节处
                // stGigEInfo.chSerialNo 偏移约 96
                // stUsb3VInfo.chSerialNo 偏移约 48
                int serialNoOffset = (deviceType == MyCamera.MV_GIGE_DEVICE) ? 96 : 48;
                IntPtr snPtr = new IntPtr(pInfo.ToInt64() + serialNoOffset);
                
                // 读取字节数组
                byte[] snBytes = new byte[32];
                Marshal.Copy(snPtr, snBytes, 0, 32);
                return System.Text.Encoding.ASCII.GetString(snBytes).TrimEnd('\0').Trim();
            }
            catch
            {
                return "";
            }
        }

        // 获取结构体字段的偏移量（备用）
        private int GetFieldOffset(Type type, string fieldName)
        {
            try
            {
                var field = type.GetField(fieldName);
                if (field != null)
                {
                    return Marshal.OffsetOf(type, fieldName).ToInt32();
                }
            }
            catch { }
            return 0;
        }

        // 断开相机
        private void DisconnectCamera()
        {
            try
            {
                // 停止采集
                if (_isGrabbing)
                {
                    _camera.MV_CC_StopGrabbing_NET();
                    _isGrabbing = false;
                }

                // 关闭设备
                _camera.MV_CC_CloseDevice_NET();

                // 销毁设备
                _camera.MV_CC_DestroyDevice_NET();

                _isConnected = false;
                AddLog("相机已断开");
                UpdateUI(false);
            }
            catch (Exception ex)
            {
                AddLog($"断开异常: {ex.Message}");
            }
        }

        // 断开连接按钮
        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            DisconnectCamera();
        }

        // 开始采集按钮
        private void BtnStartGrab_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                MessageBox.Show("请先连接相机！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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
                _grabThread = new Thread(GrabImageThread);
                _grabThread.IsBackground = true;
                _grabThread.Start();

                UpdateUI(true);
            }
            catch (Exception ex)
            {
                AddLog($"开始采集异常: {ex.Message}");
            }
        }

        // 停止采集按钮
        private void BtnStopGrab_Click(object sender, RoutedEventArgs e)
        {
            if (!_isGrabbing)
                return;

            try
            {
                // 停止采集线程
                _isGrabbing = false;
                if (_grabThread != null)
                {
                    _grabThread.Join(1000);
                    _grabThread = null;
                }

                // 停止采集
                _camera.MV_CC_StopGrabbing_NET();
                AddLog("已停止采集");
                UpdateUI(true);
            }
            catch (Exception ex)
            {
                AddLog($"停止采集异常: {ex.Message}");
            }
        }

        // 清空日志按钮
        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogText.Text = "";
        }

        // 采集线程
        private void GrabImageThread()
        {
            MyCamera.MV_FRAME_OUT frameOut = new MyCamera.MV_FRAME_OUT();
            int frameCount = 0;
            int width = 2448;  // 默认分辨率
            int height = 2048;

            while (_isGrabbing && _isConnected)
            {
                try
                {
                    // 获取图像
                    int nRet = _camera.MV_CC_GetImageBuffer_NET(ref frameOut, 1000);
                    if (nRet == 0)
                    {
                        frameCount++;
                        
                        // 获取图像尺寸
                        IntPtr pAddr = frameOut.pBufAddr;
                        if (pAddr != IntPtr.Zero)
                        {
                            // 复制图像数据
                            int frameLen = width * height * 3;
                            byte[] imageData = new byte[frameLen];
                            Marshal.Copy(pAddr, imageData, 0, frameLen);
                            
                            // 显示图像
                            DisplayImage(imageData, width, height);
                            
                            // 更新帧计数
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                FrameCountText.Text = $"帧数: {frameCount}";
                            });
                        }

                        // 释放缓存
                        _camera.MV_CC_FreeImageBuffer_NET(ref frameOut);
                    }
                    else
                    {
                        Thread.Sleep(10);
                    }
                }
                catch (Exception)
                {
                    Thread.Sleep(10);
                }
            }
        }

        // 显示图像
        private void DisplayImage(byte[] imageData, int width, int height)
        {
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    int stride = (width * 3 + 3) & ~3;
                    
                    BitmapSource bitmap = BitmapSource.Create(
                        width, height,
                        96, 96,
                        PixelFormats.Bgr24,
                        null,
                        imageData,
                        stride * height
                    );
                    
                    CameraImage.Source = bitmap;
                });
            }
            catch (Exception)
            {
                // 忽略显示错误
            }
        }

        // 窗口关闭时清理
        public void Cleanup()
        {
            if (_isGrabbing)
            {
                _isGrabbing = false;
                if (_grabThread != null)
                {
                    _grabThread.Join(1000);
                    _grabThread = null;
                }
            }

            if (_isConnected)
            {
                try
                {
                    _camera.MV_CC_StopGrabbing_NET();
                    _camera.MV_CC_CloseDevice_NET();
                    _camera.MV_CC_DestroyDevice_NET();
                }
                catch { }
            }
        }

        // 设置相机配置（由MainWindow调用）
        public void SetCameraConfig(int deviceType, string serialNo, string ipAddress)
        {
            _serialNo = serialNo;
            AddLog($"相机配置: 序列号={serialNo}");
        }
    }
}
