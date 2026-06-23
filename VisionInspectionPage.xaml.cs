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
        
        // 模拟模式
        private bool _useSimulation = false;

        public VisionInspectionPage()
        {
            InitializeComponent();
        }

        // 页面加载事件
        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            AddLog("质检与监控页面已加载");
        }

        // 连接相机
        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            AddLog("正在连接相机...");
            
            try
            {
                int nRet = ConnectWithSDK();
                
                if (nRet == 0)
                {
                    AddLog("相机连接成功！");
                    _isConnected = true;
                    UpdateConnectionStatus("状态: 已连接", Color.FromRgb(78, 205, 196));
                    UpdateButtonState(false, true, false);
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
            
            IntPtr pDeviceInfo = stDeviceList.pDeviceInfo[0];
            MV_CC_DEVICE_INFO deviceInfo = (MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MV_CC_DEVICE_INFO));
            
            string serialNo = Encoding.ASCII.GetString(deviceInfo.chSerialNo).Trim('\0');
            string model = Encoding.ASCII.GetString(deviceInfo.chModel).Trim('\0');
            
            UpdateCameraInfo(model, serialNo);
            
            nRet = MV_CC_CreateHandle_NET(ref _cameraHandle, ref deviceInfo);
            if (nRet != 0)
            {
                AddLog("创建句柄失败");
                return -1;
            }
            
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
            AddLog("模拟模式: 已连接");
            UpdateConnectionStatus("状态: 已连接(模拟)", Color.FromRgb(255, 193, 7));
            UpdateCameraInfo("模拟相机", "SIM-000001");
            UpdateButtonState(false, true, false);
        }

        // 开始采集
        private void BtnStartGrab_Click(object sender, RoutedEventArgs e)
        {
            if (_useSimulation)
            {
                StartSimulationGrab();
            }
            else
            {
                StartRealGrab();
            }
        }

        private void StartRealGrab()
        {
            try
            {
                int nRet = MV_CC_StartGrabbing_NET(_cameraHandle);
                if (nRet != 0)
                {
                    AddLog("开始采集失败");
                    return;
                }
                
                _isGrabbing = true;
                _startTime = DateTime.Now;
                _frameCount = 0;
                _stopEvent.Reset();
                
                _grabThread = new Thread(GrabThread);
                _grabThread.IsBackground = true;
                _grabThread.Start();
                
                AddLog("开始采集...");
                UpdateButtonState(false, false, true);
            }
            catch (Exception ex)
            {
                AddLog("开始采集异常: " + ex.Message);
            }
        }

        private void StartSimulationGrab()
        {
            _isGrabbing = true;
            _startTime = DateTime.Now;
            _frameCount = 0;
            _stopEvent.Reset();
            
            _grabThread = new Thread(SimulationThread);
            _grabThread.IsBackground = true;
            _grabThread.Start();
            
            AddLog("模拟模式: 开始采集...");
            UpdateButtonState(false, false, true);
        }

        private void GrabThread()
        {
            MV_FRAME_OUT stFrameOut = new MV_FRAME_OUT();
            int width = 1280;
            int height = 720;
            
            Dispatcher.Invoke(new Action(() =>
            {
                _writeableBmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                CameraImage.Source = _writeableBmp;
            }));

            while (_isGrabbing && !_stopEvent.WaitOne(10))
            {
                try
                {
                    int nRet = MV_CC_GetImageBuffer_NET(_cameraHandle, ref stFrameOut, 1000);
                    if (nRet == 0)
                    {
                        Dispatcher.Invoke(new Action(() =>
                        {
                            _frameCount++;
                            UpdateFrameCount();
                        }));
                        
                        MV_CC_FreeImageBuffer_NET(_cameraHandle, ref stFrameOut);
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

        private void SimulationThread()
        {
            int width = 800;
            int height = 450;
            
            Dispatcher.Invoke(new Action(() =>
            {
                _writeableBmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                CameraImage.Source = _writeableBmp;
            }));

            Random rand = new Random();
            int frameCount = 0;

            while (_isGrabbing && !_stopEvent.WaitOne(50))
            {
                try
                {
                    byte[] pixels = new byte[width * height * 4];
                    
                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        byte gray = (byte)(100 + rand.Next(50));
                        pixels[i] = gray;     // B
                        pixels[i + 1] = gray; // G
                        pixels[i + 2] = gray; // R
                        pixels[i + 3] = 255;  // A
                    }
                    
                    Dispatcher.Invoke(new Action(() =>
                    {
                        try
                        {
                            _writeableBmp.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
                            
                            frameCount++;
                            _frameCount = frameCount;
                            UpdateFrameCount();
                            
                            double fps = frameCount / (DateTime.Now - _startTime).TotalSeconds;
                            FpsText.Text = string.Format("帧率: {0:F1} fps", fps);
                        }
                        catch
                        {
                        }
                    }));
                }
                catch
                {
                }
            }
        }

        // 停止采集
        private void BtnStopGrab_Click(object sender, RoutedEventArgs e)
        {
            StopGrabbing();
        }

        private void StopGrabbing()
        {
            _isGrabbing = false;
            _stopEvent.Set();
            
            if (_grabThread != null && _grabThread.IsAlive)
            {
                _grabThread.Join(1000);
            }
            
            if (!_useSimulation && _cameraHandle != IntPtr.Zero)
            {
                MV_CC_StopGrabbing_NET(_cameraHandle);
            }
            
            AddLog("已停止采集");
            UpdateButtonState(true, true, false);
        }

        // 断开连接
        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            Disconnect();
        }

        private void Disconnect()
        {
            StopGrabbing();
            
            if (!_useSimulation && _cameraHandle != IntPtr.Zero)
            {
                MV_CC_CloseDevice_NET(_cameraHandle);
                MV_CC_DestroyHandle_NET(_cameraHandle);
                _cameraHandle = IntPtr.Zero;
            }
            
            _isConnected = false;
            _useSimulation = false;
            
            AddLog("已断开连接");
            UpdateConnectionStatus("状态: 未连接", Colors.Gray);
            UpdateCameraInfo("", "");
            UpdateButtonState(true, false, false);
            
            Dispatcher.Invoke(new Action(() =>
            {
                CameraImage.Source = null;
                PreviewPlaceholder.Visibility = Visibility.Visible;
            }));
        }

        // UI更新方法
        private void AddLog(string message)
        {
            try
            {
                string time = DateTime.Now.ToString("HH:mm:ss");
                string logLine = "[" + time + "] " + message + "\n";
                
                Dispatcher.Invoke(new Action(() =>
                {
                    LogText.Text += logLine;
                    LogText.ScrollToEnd();
                }));
            }
            catch
            {
            }
        }

        private void UpdateConnectionStatus(string status, Color color)
        {
            try
            {
                Dispatcher.Invoke(new Action(() =>
                {
                    ConnectionStatusText.Text = status;
                    ConnectionStatusText.Foreground = new SolidColorBrush(color);
                }));
            }
            catch
            {
            }
        }

        private void UpdateCameraInfo(string model, string serial)
        {
            try
            {
                Dispatcher.Invoke(new Action(() =>
                {
                    CameraInfoText.Text = "型号: " + model;
                    IpText.Text = "序列号: " + serial;
                }));
            }
            catch
            {
            }
        }

        private void UpdateFrameCount()
        {
            try
            {
                FrameCountText.Text = "帧数: " + _frameCount;
            }
            catch
            {
            }
        }

        private void UpdateButtonState(bool connectEnabled, bool startEnabled, bool stopEnabled)
        {
            try
            {
                Dispatcher.Invoke(new Action(() =>
                {
                    BtnConnect.IsEnabled = connectEnabled;
                    BtnStartGrab.IsEnabled = startEnabled;
                    BtnStopGrab.IsEnabled = stopEnabled;
                    BtnDisconnect.IsEnabled = _isConnected;
                }));
            }
            catch
            {
            }
        }

        // 清空日志
        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogText.Text = "";
            AddLog("日志已清空");
        }

        // 页面卸载
        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            Disconnect();
        }

        // 设置相机信息（供外部调用）
        public void SetCameraInfo(string ip, string serial)
        {
            try
            {
                _cameraIP = ip;
                _cameraSerial = serial;
                Application.Current.Dispatcher.Invoke(new Action(() =>
                {
                    if (IpText != null)
                        IpText.Text = string.IsNullOrEmpty(ip) ? "未设置" : ip;
                }));
            }
            catch
            {
            }
        }
    }
}
