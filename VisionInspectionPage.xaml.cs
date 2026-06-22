using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace WpfApp1
{
    public partial class VisionInspectionPage : Page
    {
        // 海康SDK设备信息
        private IntPtr _deviceHandle = IntPtr.Zero;
        private bool _isConnected = false;
        private bool _isGrabbing = false;
        private Thread _grabThread = null;
        private ManualResetEvent _stopEvent = new ManualResetEvent(false);
        
        // 相机配置信息
        private string _cameraSerial = "";
        private string _cameraIP = "";

        // SDK DLL路径
        private static readonly string SDK_DLL_PATH = GetSdkDllPath();

        private static string GetSdkDllPath()
        {
            // 尝试多个可能的SDK安装路径
            string[] possiblePaths = new string[]
            {
                @"C:\Program Files (x86)\MVS\Development\Lib\win64",
                @"C:\Program Files\MVS\Development\Lib\win64",
                @"C:\Program Files (x86)\MVS\Development\Lib\Win64",
                @"C:\MVS\Development\Lib\win64",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SDK")
            };

            foreach (string path in possiblePaths)
            {
                string dllPath = Path.Combine(path, "MvCameraControl.Net.dll");
                if (File.Exists(dllPath))
                {
                    return dllPath;
                }
            }

            // 如果都没找到，返回不带路径的DLL名，让系统搜索PATH
            return "MvCameraControl.Net.dll";
        }

        public VisionInspectionPage()
        {
            InitializeComponent();
            UpdateTime();
            
            // 页面加载时从配置页面获取相机信息
            Loaded += VisionInspectionPage_Loaded;
        }

        private void VisionInspectionPage_Loaded(object sender, RoutedEventArgs e)
        {
            // 从配置页面获取相机信息
            var configPage = new ParameterConfigPage();
            configPage.LoadConfig();
            
            _cameraSerial = configPage.GetCameraSerial();
            _cameraIP = configPage.GetCameraIP();
            
            if (!string.IsNullOrEmpty(_cameraSerial))
            {
                SerialNoText.Text = "序列号: " + _cameraSerial;
            }
            else if (!string.IsNullOrEmpty(_cameraIP))
            {
                SerialNoText.Text = "IP地址: " + _cameraIP;
            }
            
            // 显示SDK路径信息
            Log("SDK DLL路径: " + SDK_DLL_PATH);
            Log("页面加载完成，等待连接相机...");
        }

        // ========== 海康SDK函数声明 ==========
        
        // 设备信息结构体
        [StructLayout(LayoutKind.Sequential)]
        public struct MV_CC_DEVICE_INFO
        {
            public ushort nMajorVer;
            public ushort nMinorVer;
            public ushort nMajorType;
            public ushort nMinorType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] chManufacturer;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] chModel;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] chSerialNumber;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] chUserDefinedName;
            public uint nNetInterface;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] chSpecificType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
            public byte[] RemoteDeviceInfo;
        }

        // 设备信息列表结构体
        [StructLayout(LayoutKind.Sequential)]
        public struct MV_CC_DEVICE_INFO_LIST
        {
            public uint nDeviceNum;
            public IntPtr pDeviceInfo;
        }

        // 回调委托
        public delegate void MV_CC_DEVICE_CALLBACL(IntPtr pData, uint nDataLen, IntPtr pUser);

        // SDK函数声明 - 使用完整路径
        [DllImport(SDK_DLL_PATH, EntryPoint = "MV_CC_EnumDevices", CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_CC_EnumDevices(uint nTLayerType, ref MV_CC_DEVICE_INFO_LIST pstDevList);

        [DllImport(SDK_DLL_PATH, EntryPoint = "MV_CC_CreateHandle", CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_CC_CreateHandle(ref IntPtr handle, ref MV_CC_DEVICE_INFO pstDevInfo);

        [DllImport(SDK_DLL_PATH, EntryPoint = "MV_CC_OpenDevice", CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_CC_OpenDevice(IntPtr handle, uint nAccessMode, ushort nSwitchMode);

        [DllImport(SDK_DLL_PATH, EntryPoint = "MV_CC_StartGrabbing", CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_CC_StartGrabbing(IntPtr handle);

        [DllImport(SDK_DLL_PATH, EntryPoint = "MV_CC_StopGrabbing", CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_CC_StopGrabbing(IntPtr handle);

        [DllImport(SDK_DLL_PATH, EntryPoint = "MV_CC_CloseDevice", CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_CC_CloseDevice(IntPtr handle);

        [DllImport(SDK_DLL_PATH, EntryPoint = "MV_CC_DestroyHandle", CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_CC_DestroyHandle(IntPtr handle);

        [DllImport(SDK_DLL_PATH, EntryPoint = "MV_CC_GetImageBuffer", CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_CC_GetImageBuffer(IntPtr handle, IntPtr pstFrame, uint nTimeout);

        [DllImport(SDK_DLL_PATH, EntryPoint = "MV_CC_FreeImageBuffer", CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_CC_FreeImageBuffer(IntPtr handle, IntPtr pstFrame);

        [DllImport(SDK_DLL_PATH, EntryPoint = "MV_CC_SetEnumValue", CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_CC_SetEnumValue(IntPtr handle, string strKey, uint nValue);

        [DllImport(SDK_DLL_PATH, EntryPoint = "MV_CC_SetFloatValue", CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_CC_SetFloatValue(IntPtr handle, string strKey, float fValue);

        // 图像帧结构体
        [StructLayout(LayoutKind.Sequential)]
        public struct MV_FRAME_OUT
        {
            public IntPtr pBufAddr;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] nFrameLen;
            public uint nWidth;
            public uint nHeight;
            public ushort nPixelType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] nFrameNum;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] nDevTimeStamp;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] nHostTimeStamp;
        }

        // ========== 相机操作方法 ==========

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            ConnectCamera();
        }

        private void ConnectCamera()
        {
            try
            {
                Log("正在枚举设备...");
                
                // 枚举设备 (USB和GIGE接口)
                MV_CC_DEVICE_INFO_LIST deviceList = new MV_CC_DEVICE_INFO_LIST();
                int nRet = MV_CC_EnumDevices(0x00000001 | 0x00000002, ref deviceList);
                
                if (nRet != 0 || deviceList.nDeviceNum == 0)
                {
                    Log("错误: 未找到相机设备，请检查相机连接");
                    MessageBox.Show("未找到相机设备，请检查相机连接！", "连接失败", 
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Log("找到 " + deviceList.nDeviceNum + " 个设备");

                // 获取设备信息
                IntPtr pDeviceInfo = deviceList.pDeviceInfo;
                MV_CC_DEVICE_INFO deviceInfo = (MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MV_CC_DEVICE_INFO));
                
                // 读取序列号
                string serialNumber = System.Text.Encoding.ASCII.GetString(deviceInfo.chSerialNumber).Trim('\0');
                Log("设备序列号: " + serialNumber);

                Dispatcher.Invoke(() =>
                {
                    SerialNoText.Text = "序列号: " + serialNumber;
                    StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF9800"));
                    CameraStatusText.Text = "正在连接...";
                });

                // 创建设备句柄
                nRet = MV_CC_CreateHandle(ref _deviceHandle, ref deviceInfo);
                if (nRet != 0)
                {
                    Log("错误: 创建设备句柄失败，错误码: 0x" + nRet.ToString("X"));
                    return;
                }

                // 打开设备
                nRet = MV_CC_OpenDevice(_deviceHandle, 1, 0);  // nAccessMode=1 (读写), nSwitchMode=0
                if (nRet != 0)
                {
                    Log("错误: 打开设备失败，错误码: 0x" + nRet.ToString("X"));
                    MV_CC_DestroyHandle(_deviceHandle);
                    _deviceHandle = IntPtr.Zero;
                    return;
                }

                _isConnected = true;
                Log("相机连接成功！");

                Dispatcher.Invoke(() =>
                {
                    StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50"));
                    CameraStatusText.Text = "相机已连接";
                    BtnConnect.IsEnabled = false;
                    BtnStartGrab.IsEnabled = true;
                    BtnStopGrab.IsEnabled = false;
                    BtnDisconnect.IsEnabled = true;
                    PreviewPlaceholder.Visibility = Visibility.Collapsed;
                });

            }
            catch (BadImageFormatException ex)
            {
                // DLL加载失败，可能是32位/64位不匹配
                string errorMsg = "海康SDK加载失败！请确保：\n\n" +
                    "1. 已安装海康机器视觉SDK (MVS)\n" +
                    "2. 项目平台目标设置为 x64（菜单: 生成 -> 配置管理器 -> 平台 -> x64）\n" +
                    "3. DLL文件路径: " + SDK_DLL_PATH + "\n\n" +
                    "4. 将SDK的DLL文件复制到程序目录:\n" +
                    "   从: C:\\Program Files (x86)\\MVS\\Development\\Lib\\win64\\\n" +
                    "   到: bin\\x64\\Debug\\";
                
                Log("错误: 海康SDK DLL加载失败！请检查SDK安装和平台设置");
                MessageBox.Show(errorMsg, "SDK加载错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (DllNotFoundException ex)
            {
                string errorMsg = "未找到海康SDK DLL文件！\n\n" + ex.Message + "\n\n" +
                    "请检查DLL文件是否存在:\n" + SDK_DLL_PATH + "\n\n" +
                    "如不存在，请安装海康机器视觉SDK (MVS)\n" +
                    "下载地址: https://www.hikvision.com/cn/support/tools/hikvision-tools/hikvision-mvs/";
                
                Log("错误: 未找到SDK DLL - " + ex.Message);
                MessageBox.Show(errorMsg, "DLL缺失", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (EntryPointNotFoundException ex)
            {
                string errorMsg = "SDK函数未找到！\n\n" + ex.Message + "\n\n" +
                    "可能的原因:\n" +
                    "1. SDK版本不匹配，请使用MVS_V3.4.0或更新版本\n" +
                    "2. DLL文件损坏，请重新安装SDK\n" +
                    "3. 当前使用的DLL: " + SDK_DLL_PATH;
                
                Log("错误: SDK函数未找到 - " + ex.Message);
                MessageBox.Show(errorMsg, "SDK版本不匹配", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                Log("连接异常: " + ex.Message);
                MessageBox.Show("连接异常: " + ex.Message, "错误", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnStartGrab_Click(object sender, RoutedEventArgs e)
        {
            StartGrabbing();
        }

        private void StartGrabbing()
        {
            if (!_isConnected || _deviceHandle == IntPtr.Zero)
            {
                Log("错误: 相机未连接");
                return;
            }

            try
            {
                Log("正在开始采集...");

                // 开始采集
                int nRet = MV_CC_StartGrabbing(_deviceHandle);
                if (nRet != 0)
                {
                    Log("错误: 开始采集失败，错误码: 0x" + nRet.ToString("X"));
                    return;
                }

                _isGrabbing = true;
                _stopEvent.Reset();

                // 启动采集线程
                _grabThread = new Thread(GrabThread);
                _grabThread.IsBackground = true;
                _grabThread.Start();

                Log("图像采集已开始");

                Dispatcher.Invoke(() =>
                {
                    BtnStartGrab.IsEnabled = false;
                    BtnStopGrab.IsEnabled = true;
                    CameraStatusText.Text = "正在采集";
                });

            }
            catch (Exception ex)
            {
                Log("采集异常: " + ex.Message);
            }
        }

        private void BtnStopGrab_Click(object sender, RoutedEventArgs e)
        {
            StopGrabbing();
        }

        private void StopGrabbing()
        {
            if (!_isGrabbing)
            {
                return;
            }

            try
            {
                Log("正在停止采集...");

                // 停止采集线程
                _isGrabbing = false;
                _stopEvent.Set();

                if (_grabThread != null && _grabThread.IsAlive)
                {
                    _grabThread.Join(1000);
                }

                // 停止SDK采集
                int nRet = MV_CC_StopGrabbing(_deviceHandle);
                if (nRet != 0)
                {
                    Log("错误: 停止采集失败，错误码: 0x" + nRet.ToString("X"));
                }

                Log("图像采集已停止");

                Dispatcher.Invoke(() =>
                {
                    BtnStartGrab.IsEnabled = true;
                    BtnStopGrab.IsEnabled = false;
                    CameraStatusText.Text = "相机已连接";
                });

            }
            catch (Exception ex)
            {
                Log("停止采集异常: " + ex.Message);
            }
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            DisconnectCamera();
        }

        private void DisconnectCamera()
        {
            try
            {
                Log("正在断开相机...");

                // 停止采集
                if (_isGrabbing)
                {
                    StopGrabbing();
                }

                // 关闭设备
                if (_deviceHandle != IntPtr.Zero)
                {
                    int nRet = MV_CC_CloseDevice(_deviceHandle);
                    if (nRet != 0)
                    {
                        Log("警告: 关闭设备失败，错误码: 0x" + nRet.ToString("X"));
                    }

                    nRet = MV_CC_DestroyHandle(_deviceHandle);
                    if (nRet != 0)
                    {
                        Log("警告: 销毁句柄失败，错误码: 0x" + nRet.ToString("X"));
                    }

                    _deviceHandle = IntPtr.Zero;
                }

                _isConnected = false;
                Log("相机已断开");

                Dispatcher.Invoke(() =>
                {
                    StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F44336"));
                    CameraStatusText.Text = "已断开";
                    BtnConnect.IsEnabled = true;
                    BtnStartGrab.IsEnabled = false;
                    BtnStopGrab.IsEnabled = false;
                    BtnDisconnect.IsEnabled = false;
                    PreviewPlaceholder.Visibility = Visibility.Visible;
                    PreviewPlaceholder.Text = "相机已断开连接";
                });

            }
            catch (Exception ex)
            {
                Log("断开连接异常: " + ex.Message);
            }
        }

        // 图像采集线程
        private void GrabThread()
        {
            int frameCount = 0;
            MV_FRAME_OUT frameOut = new MV_FRAME_OUT();

            while (_isGrabbing && !_stopEvent.WaitOne(10))
            {
                try
                {
                    int nRet = MV_CC_GetImageBuffer(_deviceHandle, IntPtr.Zero, 1000);
                    
                    if (nRet == 0)
                    {
                        frameCount++;
                        
                        // 更新显示
                        Dispatcher.Invoke(() =>
                        {
                            // 更新帧计数
                            CameraStatusText.Text = "采集中 - " + frameCount + " 帧";
                        });
                    }
                }
                catch (Exception ex)
                {
                    if (_isGrabbing)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            Log("采集异常: " + ex.Message);
                        });
                    }
                    break;
                }
            }

            Log("采集线程结束，共采集 " + frameCount + " 帧");
        }

        // 曝光值改变
        private void ExposureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ExposureValueText == null) return;
            
            double value = Math.Round(e.NewValue, 1);
            ExposureValueText.Text = value.ToString("F1") + " ms";
            
            if (_isConnected && _deviceHandle != IntPtr.Zero)
            {
                try
                {
                    MV_CC_SetFloatValue(_deviceHandle, "ExposureTime", (float)value * 1000);
                }
                catch
                {
                    // 忽略设置失败
                }
            }
        }

        // 增益值改变
        private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (GainValueText == null) return;
            
            double value = Math.Round(e.NewValue, 1);
            GainValueText.Text = value.ToString("F1") + " dB";
            
            if (_isConnected && _deviceHandle != IntPtr.Zero)
            {
                try
                {
                    MV_CC_SetFloatValue(_deviceHandle, "Gain", (float)value);
                }
                catch
                {
                    // 忽略设置失败
                }
            }
        }

        // 更新时间
        private void UpdateTime()
        {
            Dispatcher.Invoke(() =>
            {
                CurrentTimeText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            });
        }

        // 日志记录
        private void Log(string message)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    string timeStamp = DateTime.Now.ToString("HH:mm:ss");
                    LogText.Text = "[" + timeStamp + "] " + message + "\n" + LogText.Text;
                    
                    // 限制日志行数
                    string[] lines = LogText.Text.Split('\n');
                    if (lines.Length > 100)
                    {
                        LogText.Text = string.Join("\n", lines, 0, 100);
                    }
                });
            }
            catch
            {
                // 忽略日志错误
            }
        }

        // 公共方法 - 设置相机信息
        public void SetCameraInfo(string serial, string ip)
        {
            _cameraSerial = serial;
            _cameraIP = ip;
        }

        protected override void OnDestructed()
        {
            // 页面销毁时断开相机连接
            if (_isConnected)
            {
                DisconnectCamera();
            }
            base.OnDestructed();
        }
    }
}
