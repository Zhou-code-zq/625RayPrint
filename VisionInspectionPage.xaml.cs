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
        private int _deviceType = 0;

        public VisionInspectionPage()
        {
            InitializeComponent();
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            LoadCameraInfo();
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
                    _deviceType = configPage.DeviceType;
                    _serialNo = configPage.SerialNo;
                    _ipAddress = configPage.IpAddress;
                    AddLog($"已加载配置：设备类型={_deviceType}，序列号={_serialNo}，IP={_ipAddress}");
                }
            });
        }

        // 添加日志
        private void AddLog(string message)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                TxtLog.Text += $"[{timestamp}] {message}\r\n";
                LogScrollViewer.ScrollToEnd();
            });
        }

        // 更新UI状态
        private void UpdateUI(bool connected)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                BtnConnect.Content = connected ? "断开相机" : "连接相机";
                BtnStartGrab.IsEnabled = connected;
                BtnStopGrab.IsEnabled = connected && _isGrabbing;
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
                
                // 尝试枚举所有类型的设备
                MyCamera.MV_CC_DEVICE_INFO_LIST deviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
                int nRet = 0;
                int deviceCount = 0;
                
                // 根据配置选择设备层
                switch (_deviceType)
                {
                    case 0:  // MV系列 -> GenTL (使用GigE枚举)
                        nRet = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE, ref deviceList);
                        if (nRet == 0)
                            deviceCount = (int)deviceList.nDeviceNum;
                        AddLog($"枚举到 {deviceCount} 个 GigE 设备");
                        break;
                    case 1:  // CA系列 -> GigE
                        nRet = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE, ref deviceList);
                        if (nRet == 0)
                            deviceCount = (int)deviceList.nDeviceNum;
                        AddLog($"枚举到 {deviceCount} 个 GigE 设备");
                        break;
                    case 2:  // CH系列 -> USB
                        nRet = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_USB_DEVICE, ref deviceList);
                        if (nRet == 0)
                            deviceCount = (int)deviceList.nDeviceNum;
                        AddLog($"枚举到 {deviceCount} 个 USB 设备");
                        break;
                    default:
                        AddLog("未知的设备类型");
                        return;
                }
                
                if (nRet != 0)
                {
                    AddLog($"枚举设备失败，错误码: {nRet}");
                    return;
                }

                if (deviceList.nDeviceNum == 0)
                {
                    AddLog("未发现设备，请检查相机连接和MVS虚拟相机是否已打开");
                    return;
                }

                // 直接使用第一个设备
                int targetIndex = 0;
                AddLog($"使用第 {targetIndex + 1} 个设备");

                // 创建相机
                IntPtr pDeviceInfo = Marshal.UnsafeAddrOfPinnedArrayElement(deviceList.pDeviceInfo, targetIndex);
                nRet = _camera.MV_CC_CreateDevice_NET(pDeviceInfo);
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

                UpdateUI(true);
            }
            catch (Exception ex)
            {
                AddLog($"连接异常: {ex.Message}");
            }
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
                        if (frameOut.pBufAddr != IntPtr.Zero && frameOut.nFrameLen > 0)
                        {
                            width = (int)frameOut.nWidth;
                            height = (int)frameOut.nHeight;
                            
                            // 复制图像数据
                            byte[] imageData = new byte[frameOut.nFrameLen];
                            Marshal.Copy(frameOut.pBufAddr, imageData, 0, (int)frameOut.nFrameLen);
                            
                            // 显示图像
                            DisplayImage(imageData, width, height);
                            
                            // 更新帧计数
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                TxtFrameCount.Text = frameCount.ToString();
                            });
                        }

                        // 释放缓存
                        _camera.MV_CC_FreeImageBuffer_NET(ref frameOut);
                    }
                    else
                    {
                        // 超时或其他错误，继续尝试
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
                    // 计算步长（4字节对齐）
                    int stride = (width * 3 + 3) & ~3;
                    
                    // 创建BitmapSource
                    BitmapSource bitmap = BitmapSource.Create(
                        width, height,
                        96, 96,
                        PixelFormats.Bgr24,
                        null,
                        imageData,
                        stride * height
                    );
                    
                    // 显示图像
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
    }
}
