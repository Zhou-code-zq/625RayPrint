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
        // 海康SDK DLL路径
        private const string SDK_DLL = @"C:\Program Files (x86)\MVS\Development\DotNet\win64\net40\MvCameraControl.Net.dll";
        
        // 海康SDK API声明
        [DllImport(SDK_DLL, EntryPoint = "MV_CC_EnumDevices_NET")]
        private static extern int MV_CC_EnumDevices_NET(uint nTLayerType, ref MV_CC_DEVICE_INFO_LIST stDeviceList);
        
        [DllImport(SDK_DLL, EntryPoint = "MV_CC_CreateHandle_NET")]
        private static extern int MV_CC_CreateHandle_NET(ref IntPtr handle, ref MV_CC_DEVICE_INFO pstDeviceInfo);
        
        [DllImport(SDK_DLL, EntryPoint = "MV_CC_OpenDevice_NET")]
        private static extern int MV_CC_OpenDevice_NET(IntPtr handle, uint nAccessMode, ushort nSwitchDefaultIP);
        
        [DllImport(SDK_DLL, EntryPoint = "MV_CC_StartGrabbing_NET")]
        private static extern int MV_CC_StartGrabbing_NET(IntPtr handle);
        
        [DllImport(SDK_DLL, EntryPoint = "MV_CC_StopGrabbing_NET")]
        private static extern int MV_CC_StopGrabbing_NET(IntPtr handle);
        
        [DllImport(SDK_DLL, EntryPoint = "MV_CC_CloseDevice_NET")]
        private static extern int MV_CC_CloseDevice_NET(IntPtr handle);
        
        [DllImport(SDK_DLL, EntryPoint = "MV_CC_DestroyHandle_NET")]
        private static extern int MV_CC_DestroyHandle_NET(IntPtr handle);
        
        [DllImport(SDK_DLL, EntryPoint = "MV_CC_GetImageBuffer_NET")]
        private static extern int MV_CC_GetImageBuffer_NET(IntPtr handle, ref MV_FRAME_OUT pstFrameOut, uint nMsec);
        
        [DllImport(SDK_DLL, EntryPoint = "MV_CC_FreeImageBuffer_NET")]
        private static extern int MV_CC_FreeImageBuffer_NET(IntPtr handle, ref MV_FRAME_OUT pstFrameOut);

        // SDK数据结构
        [StructLayout(LayoutKind.Sequential)]
        public struct MV_CC_DEVICE_INFO_LIST
        {
            public uint nDeviceNum;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public IntPtr[] pDeviceInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MV_CC_DEVICE_INFO
        {
            public uint nDeviceType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] chManufacturer;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] chModel;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] chSerialNo;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] chUserDefinedName;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] chDeviceAddress;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MV_FRAME_OUT
        {
            public IntPtr pBufAddr;
            public uint nWidth;
            public uint nHeight;
            public uint nFrameLen;
            public uint nFrameNum;
            public uint nPixelType;
            public uint nFrameLen_Original;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public uint[] nDevTimeStamp;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public uint[] nHostTimeStamp;
        }

        private const uint MV_GIGE_DEVICE = 1;
        private const uint MV_ACCESS_Exclusive = 1;

        // 变量
        private IntPtr _cameraHandle = IntPtr.Zero;
        private bool _isConnected = false;
        private bool _isGrabbing = false;
        private Thread _grabThread = null;
        private ManualResetEvent _stopEvent = new ManualResetEvent(false);
        private WriteableBitmap _writeableBmp = null;
        private int _frameCount = 0;
        private DateTime _startTime;
        
        // 模拟模式（当SDK不可用时）
        private bool _useSimulation = false;

        public VisionInspectionPage()
        {
            InitializeComponent();
            Loaded += VisionInspectionPage_Loaded;
        }

        private void VisionInspectionPage_Loaded(object sender, RoutedEventArgs e)
        {
            AddLog("质检与监控页面已加载");
        }

        // 连接相机
        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            AddLog("正在连接相机...");
            
            try
            {
                // 尝试使用真实SDK
                int nRet = ConnectWithSDK();
                
                if (nRet == 0)
                {
                    AddLog("相机连接成功！");
                    _isConnected = true;
                    ConnectionStatusText.Text = "状态: 已连接";
                    ConnectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(78, 205, 196));
                    
                    BtnConnect.IsEnabled = false;
                    BtnStartGrab.IsEnabled = true;
                    BtnDisconnect.IsEnabled = true;
                }
                else
                {
                    AddLog("SDK连接失败，启用模拟模式");
                    ConnectWithSimulation();
                }
            }
            catch (DllNotFoundException)
            {
                AddLog("未找到海康SDK，启用模拟模式");
                ConnectWithSimulation();
            }
            catch (Exception ex)
            {
                AddLog("连接异常: " + ex.Message);
                ConnectWithSimulation();
            }
        }

        private int ConnectWithSDK()
        {
            MV_CC_DEVICE_INFO_LIST stDeviceList = new MV_CC_DEVICE_INFO_LIST();
            stDeviceList.pDeviceInfo = new IntPtr[16];
            
            int nRet = MV_CC_EnumDevices_NET(MV_GIGE_DEVICE, ref stDeviceList);
            if (nRet != 0 || stDeviceList.nDeviceNum == 0)
            {
                AddLog("未发现设备，设备数量: " + stDeviceList.nDeviceNum);
                return -1;
            }
            
            AddLog("发现 " + stDeviceList.nDeviceNum + " 个设备");
            
            // 获取第一个设备信息
            IntPtr pDeviceInfo = stDeviceList.pDeviceInfo[0];
            MV_CC_DEVICE_INFO deviceInfo = (MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MV_CC_DEVICE_INFO));
            
            // 显示设备信息
            string serialNo = Encoding.ASCII.GetString(deviceInfo.chSerialNo).Trim('\0');
            string model = Encoding.ASCII.GetString(deviceInfo.chModel).Trim('\0');
            CameraInfoText.Text = "型号: " + model;
            IpText.Text = "序列号: " + serialNo;
            
            // 创建句柄
            nRet = MV_CC_CreateHandle_NET(ref _cameraHandle, ref deviceInfo);
            if (nRet != 0)
            {
                AddLog("创建句柄失败");
                return -1;
            }
            
            // 打开设备
            nRet = MV_CC_OpenDevice_NET(_cameraHandle, MV_ACCESS_Exclusive, 0);
            if (nRet != 0)
            {
                AddLog("打开设备失败");
                MV_CC_DestroyHandle_NET(_cameraHandle);
                return -1;
            }
            
            return 0;
        }

        private void ConnectWithSimulation()
        {
            _useSimulation = true;
            _isConnected = true;
            ConnectionStatusText.Text = "状态: 模拟模式";
            ConnectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 230, 109));
            CameraInfoText.Text = "型号: MV-CA016-10GM (模拟)";
            IpText.Text = "序列号: 00-xx-xx-xx-xx-xx (模拟)";
            
            BtnConnect.IsEnabled = false;
            BtnStartGrab.IsEnabled = true;
            BtnDisconnect.IsEnabled = true;
            
            AddLog("模拟相机连接成功");
        }

        // 开始采集
        private void BtnStartGrab_Click(object sender, RoutedEventArgs e)
        {
            if (_useSimulation)
            {
                StartSimulationGrab();
                return;
            }
            
            AddLog("开始采集...");
            
            int nRet = MV_CC_StartGrabbing_NET(_cameraHandle);
            if (nRet != 0)
            {
                AddLog("开始采集失败");
                return;
            }
            
            _isGrabbing = true;
            _frameCount = 0;
            _startTime = DateTime.Now;
            _stopEvent.Reset();
            
            // 启动采集线程
            _grabThread = new Thread(GrabThreadProc);
            _grabThread.IsBackground = true;
            _grabThread.Start();
            
            BtnStartGrab.IsEnabled = false;
            BtnStopGrab.IsEnabled = true;
            AddLog("采集已开始");
        }

        private void StartSimulationGrab()
        {
            _isGrabbing = true;
            _frameCount = 0;
            _startTime = DateTime.Now;
            
            // 初始化显示
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            CameraImage.Visibility = Visibility.Visible;
            
            // 创建模拟图像
            int width = 800;
            int height = 600;
            _writeableBmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Gray8, null);
            CameraImage.Source = _writeableBmp;
            
            // 启动模拟线程
            _stopEvent.Reset();
            _grabThread = new Thread(SimulationThreadProc);
            _grabThread.IsBackground = true;
            _grabThread.Start();
            
            BtnStartGrab.IsEnabled = false;
            BtnStopGrab.IsEnabled = true;
            AddLog("模拟采集已开始");
        }

        private void SimulationThreadProc()
        {
            Random rand = new Random();
            
            while (!_stopEvent.WaitOne(100))
            {
                try
                {
                    // 生成模拟图像数据
                    int width = 800;
                    int height = 600;
                    byte[] buffer = new byte[width * height];
                    
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        int x = i % width;
                        int y = i / width;
                        
                        // 生成条纹图案
                        if ((y / 20) % 2 == 0)
                        {
                            buffer[i] = (byte)(150 + rand.Next(-30, 30));
                        }
                        else
                        {
                            buffer[i] = (byte)(100 + rand.Next(-20, 20));
                        }
                        
                        // 添加噪点
                        if (rand.Next(100) < 2)
                        {
                            buffer[i] = (byte)rand.Next(256);
                        }
                    }
                    
                    // 更新图像
                    Dispatcher.Invoke(() =>
                    {
                        if (_writeableBmp != null)
                        {
                            _writeableBmp.WritePixels(new Int32Rect(0, 0, width, height), buffer, width, 0);
                        }
                        
                        // 更新帧数
                        _frameCount++;
                        FrameCountText.Text = "帧数: " + _frameCount;
                        double fps = _frameCount / (DateTime.Now - _startTime).TotalSeconds;
                        FpsText.Text = "帧率: " + fps.ToString("F1") + " FPS";
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AddLog("采集异常: " + ex.Message));
                }
            }
        }

        private void GrabThreadProc()
        {
            MV_FRAME_OUT stFrameOut = new MV_FRAME_OUT();
            byte[] buffer = null;
            
            while (!_stopEvent.WaitOne(100))
            {
                try
                {
                    int nRet = MV_CC_GetImageBuffer_NET(_cameraHandle, ref stFrameOut, 1000);
                    if (nRet == 0)
                    {
                        // 复制图像数据
                        if (buffer == null || buffer.Length != (int)stFrameOut.nFrameLen)
                        {
                            buffer = new byte[stFrameOut.nFrameLen];
                        }
                        Marshal.Copy(stFrameOut.pBufAddr, buffer, 0, (int)stFrameOut.nFrameLen);
                        
                        // 释放缓冲区
                        MV_CC_FreeImageBuffer_NET(_cameraHandle, ref stFrameOut);
                        
                        // 更新UI
                        Dispatcher.Invoke(() =>
                        {
                            UpdateImage(buffer, (int)stFrameOut.nWidth, (int)stFrameOut.nHeight);
                            
                            _frameCount++;
                            FrameCountText.Text = "帧数: " + _frameCount;
                            double fps = _frameCount / (DateTime.Now - _startTime).TotalSeconds;
                            FpsText.Text = "帧率: " + fps.ToString("F1") + " FPS";
                        });
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AddLog("采集异常: " + ex.Message));
                }
            }
        }

        private void UpdateImage(byte[] buffer, int width, int height)
        {
            if (PreviewPlaceholder.Visibility == Visibility.Visible)
            {
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
                CameraImage.Visibility = Visibility.Visible;
            }
            
            if (_writeableBmp == null || _writeableBmp.PixelWidth != width || _writeableBmp.PixelHeight != height)
            {
                _writeableBmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Gray8, null);
                CameraImage.Source = _writeableBmp;
            }
            
            _writeableBmp.WritePixels(new Int32Rect(0, 0, width, height), buffer, width, 0);
        }

        // 停止采集
        private void BtnStopGrab_Click(object sender, RoutedEventArgs e)
        {
            if (_useSimulation)
            {
                StopSimulationGrab();
                return;
            }
            
            AddLog("停止采集...");
            _stopEvent.Set();
            
            if (_grabThread != null && _grabThread.IsAlive)
            {
                _grabThread.Join(1000);
            }
            
            MV_CC_StopGrabbing_NET(_cameraHandle);
            
            _isGrabbing = false;
            BtnStartGrab.IsEnabled = true;
            BtnStopGrab.IsEnabled = false;
            AddLog("采集已停止");
        }

        private void StopSimulationGrab()
        {
            _stopEvent.Set();
            
            if (_grabThread != null && _grabThread.IsAlive)
            {
                _grabThread.Join(1000);
            }
            
            _isGrabbing = false;
            BtnStartGrab.IsEnabled = true;
            BtnStopGrab.IsEnabled = false;
            AddLog("模拟采集已停止");
        }

        // 断开连接
        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            AddLog("断开连接...");
            
            if (_isGrabbing)
            {
                BtnStopGrab_Click(sender, e);
            }
            
            if (!_useSimulation)
            {
                if (_cameraHandle != IntPtr.Zero)
                {
                    MV_CC_CloseDevice_NET(_cameraHandle);
                    MV_CC_DestroyHandle_NET(_cameraHandle);
                    _cameraHandle = IntPtr.Zero;
                }
            }
            
            _isConnected = false;
            _useSimulation = false;
            
            // 重置UI
            ConnectionStatusText.Text = "状态: 未连接";
            ConnectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 107, 107));
            CameraInfoText.Text = "相机信息: --";
            IpText.Text = "IP: --";
            FrameCountText.Text = "帧数: 0";
            FpsText.Text = "帧率: 0 FPS";
            
            PreviewPlaceholder.Visibility = Visibility.Visible;
            CameraImage.Visibility = Visibility.Collapsed;
            _writeableBmp = null;
            
            BtnConnect.IsEnabled = true;
            BtnStartGrab.IsEnabled = false;
            BtnStopGrab.IsEnabled = false;
            BtnDisconnect.IsEnabled = false;
            
            AddLog("已断开连接");
        }

        // 曝光调节
        private void ExposureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ExposureValueText != null)
            {
                ExposureValueText.Text = ((int)ExposureSlider.Value) + " ms";
            }
        }

        // 增益调节
        private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (GainValueText != null)
            {
                GainValueText.Text = ((int)GainSlider.Value) + " dB";
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
            string time = DateTime.Now.ToString("HH:mm:ss");
            LogText.Text += "[" + time + "] " + message + "\n";
            
            // 滚动到底部
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LogScrollViewer.ScrollToEnd();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
