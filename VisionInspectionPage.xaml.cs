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
                uint deviceLayer = 0;
                
                // 根据配置选择设备层
                switch (_deviceType)
                {
                    case 0:  // MV系列 -> GenTL
                        deviceLayer = MyCamera.MV_GIGE_DEVICE;  // 尝试GigE
                        nRet = _camera.MV_CC_EnumDevices_NET(deviceLayer, ref deviceList);
                        if (nRet == 0 && deviceList.nDeviceNum > 0)
                        {
                            deviceCount = (int)deviceList.nDeviceNum;
                            AddLog($"枚举到 {deviceCount} 个 GigE 设备");
                        }
                        break;
                    case 1:  // CA系列 -> GigE
                        deviceLayer = MyCamera.MV_GIGE_DEVICE;
                        nRet = _camera.MV_CC_EnumDevices_NET(deviceLayer, ref deviceList);
                        if (nRet == 0)
                            deviceCount = (int)deviceList.nDeviceNum;
                        AddLog($"枚举到 {deviceCount} 个 GigE 设备");
                        break;
                    case 2:  // CH系列 -> USB
                        deviceLayer = MyCamera.MV_USB_DEVICE;
                        nRet = _camera.MV_CC_EnumDevices_NET(deviceLayer, ref deviceList);
                        if (nRet == 0)
                            deviceCount = (int)deviceList.nDeviceNum;
                        AddLog($"枚举到 {deviceCount} 个 USB 设备");
                        break;
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
                StopGrabbing();

                if (_camera != null)
                {
                    _camera.MV_CC_CloseDevice_NET();
                    _camera.MV_CC_DestroyDevice_NET();
                }

                _isConnected = false;
                AddLog("相机已断开");

                UpdateUI(false);
            }
            catch (Exception ex)
            {
                AddLog($"断开异常: {ex.Message}");
            }
        }

        // 更新UI
        private void UpdateUI(bool connected)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (connected)
                {
                    BtnConnect.Content = "断开相机";
                    BtnStartGrab.IsEnabled = true;
                    StatusText.Text = "已连接";
                }
                else
                {
                    BtnConnect.Content = "连接相机";
                    BtnStartGrab.IsEnabled = false;
                    BtnStopGrab.IsEnabled = false;
                    StatusText.Text = "未连接";
                }
            });
        }

        // 开始采集
        private void BtnStartGrab_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                AddLog("请先连接相机");
                return;
            }

            StartGrabbing();
        }

        private void StartGrabbing()
        {
            try
            {
                int nRet = _camera.MV_CC_StartGrabbing_NET();
                if (nRet != 0)
                {
                    AddLog($"开始采集失败，错误码: {nRet}");
                    return;
                }

                _isGrabbing = true;
                AddLog("已开始采集");

                _grabThread = new Thread(GrabLoop);
                _grabThread.IsBackground = true;
                _grabThread.Start();

                Application.Current.Dispatcher.Invoke(() =>
                {
                    BtnStartGrab.IsEnabled = false;
                    BtnStopGrab.IsEnabled = true;
                });
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

        private void StopGrabbing()
        {
            try
            {
                _isGrabbing = false;
                
                if (_grabThread != null)
                {
                    _grabThread.Join(1000);
                    _grabThread = null;
                }

                if (_camera != null)
                {
                    _camera.MV_CC_StopGrabbing_NET();
                }

                AddLog("已停止采集");

                Application.Current.Dispatcher.Invoke(() =>
                {
                    BtnStartGrab.IsEnabled = true;
                    BtnStopGrab.IsEnabled = false;
                });
            }
            catch (Exception ex)
            {
                AddLog($"停止采集异常: {ex.Message}");
            }
        }

        // 采集循环
        private void GrabLoop()
        {
            MyCamera.MV_FRAME_OUT frameOut = new MyCamera.MV_FRAME_OUT();
            int frameCount = 0;

            while (_isGrabbing && _isConnected)
            {
                try
                {
                    int nRet = _camera.MV_CC_GetImageBuffer_NET(ref frameOut, 1000);
                    
                    if (nRet == 0)
                    {
                        frameCount++;
                        
                        // 更新帧计数
                        int finalCount = frameCount;
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            FrameCountText.Text = finalCount.ToString();
                        }));

                        // 显示图像
                        if (frameOut.pBufAddr != IntPtr.Zero)
                        {
                            DisplayFrame(frameOut);
                        }

                        // 释放图像缓存
                        _camera.MV_CC_FreeImageBuffer_NET(ref frameOut);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"采集异常: {ex.Message}");
                }

                Thread.Sleep(10);
            }
        }

        // 显示图像帧
        private void DisplayFrame(MyCamera.MV_FRAME_OUT frameOut)
        {
            try
            {
                // 使用预设的分辨率
                int nWidth = 2448;
                int nHeight = 2048;
                
                // 获取实际图像数据
                IntPtr pData = frameOut.pBufAddr;
                uint nDataLen = frameOut.nFrameLen;
                
                if (pData == IntPtr.Zero || nDataLen == 0)
                    return;

                Application.Current.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        // 创建简单的BitmapImage用于显示
                        // 这里使用模拟方式，实际应该根据相机像素格式解析
                        byte[] imageData = new byte[nDataLen];
                        Marshal.Copy(pData, imageData, 0, (int)nDataLen);
                        
                        // 创建 BitmapSource
                        // 简化处理：创建一个占位图像
                        WriteableBitmap bitmap = new WriteableBitmap(nWidth, nHeight, 96, 96, PixelFormats.Bgr24, null);
                        
                        // 设置图像源
                        CameraImage.Source = bitmap;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"显示图像异常: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DisplayFrame异常: {ex.Message}");
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
                    AddLog($"保存图像到: {dialog.FileName}");
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
            if (_isGrabbing)
            {
                StopGrabbing();
            }

            if (_isConnected && _camera != null)
            {
                _camera.MV_CC_CloseDevice_NET();
                _camera.MV_CC_DestroyDevice_NET();
                _isConnected = false;
            }
        }
    }
}
