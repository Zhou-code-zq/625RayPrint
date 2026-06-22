using System;
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

        // SDK函数声明
        [DllImport("MvCameraControl.Net.dll", EntryPoint = "MV_CC_EnumDevices")]
        private static extern int MV_CC_EnumDevices(uint nTLayerType, ref MV_CC_DEVICE_INFO_LIST pstDevList);

        [DllImport("MvCameraControl.Net.dll", EntryPoint = "MV_CC_CreateHandle")]
        private static extern int MV_CC_CreateHandle(ref IntPtr handle, ref MV_CC_DEVICE_INFO pstDevInfo);

        [DllImport("MvCameraControl.Net.dll", EntryPoint = "MV_CC_OpenDevice")]
        private static extern int MV_CC_OpenDevice(IntPtr handle, uint nAccessMode, ushort nSwitchMode);

        [DllImport("MvCameraControl.Net.dll", EntryPoint = "MV_CC_StartGrabbing")]
        private static extern int MV_CC_StartGrabbing(IntPtr handle);

        [DllImport("MvCameraControl.Net.dll", EntryPoint = "MV_CC_StopGrabbing")]
        private static extern int MV_CC_StopGrabbing(IntPtr handle);

        [DllImport("MvCameraControl.Net.dll", EntryPoint = "MV_CC_CloseDevice")]
        private static extern int MV_CC_CloseDevice(IntPtr handle);

        [DllImport("MvCameraControl.Net.dll", EntryPoint = "MV_CC_DestroyHandle")]
        private static extern int MV_CC_DestroyHandle(IntPtr handle);

        [DllImport("MvCameraControl.Net.dll", EntryPoint = "MV_CC_GetImageBuffer")]
        private static extern int MV_CC_GetImageBuffer(IntPtr handle, IntPtr pstFrame, uint nTimeout);

        [DllImport("MvCameraControl.Net.dll", EntryPoint = "MV_CC_FreeImageBuffer")]
        private static extern int MV_CC_FreeImageBuffer(IntPtr handle, IntPtr pstFrame);

        [DllImport("MvCameraControl.Net.dll", EntryPoint = "MV_CC_SetEnumValue")]
        private static extern int MV_CC_SetEnumValue(IntPtr handle, string strKey, uint nValue);

        [DllImport("MvCameraControl.Net.dll", EntryPoint = "MV_CC_SetFloatValue")]
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
            catch (BadImageFormatException)
            {
                // DLL加载失败，可能是32位/64位不匹配
                string errorMsg = "海康SDK加载失败！请确保：\n\n" +
                    "1. 已安装海康机器视觉SDK (MVS)\n" +
                    "2. 项目平台目标设置为 x64（菜单: 生成 -> 配置管理器 -> 平台 -> x64）\n" +
                    "3. MVS安装目录下的DLL文件已复制到程序运行目录\n\n" +
                    "默认DLL位置: C:\\Program Files (x86)\\MVS\\Development\\Lib\\win64";
                
                Log("错误: 海康SDK DLL加载失败！请检查SDK安装和平台设置");
                MessageBox.Show(errorMsg, "SDK加载错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (DllNotFoundException ex)
            {
                string errorMsg = "未找到海康SDK DLL文件！\n\n" + ex.Message + "\n\n" +
                    "请安装海康机器视觉SDK (MVS)\n" +
                    "下载地址: https://www.hikvision.com/cn/support/tools/hikvision-tools/hikvision-mvs/";
                
                Log("错误: 未找到SDK DLL - " + ex.Message);
                MessageBox.Show(errorMsg, "DLL缺失", MessageBoxButton.OK, MessageBoxImage.Error);
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
                _grabThread = new Thread(GrabThreadProc);
                _grabThread.IsBackground = true;
                _grabThread.Start();

                Log("图像采集已启动");

                Dispatcher.Invoke(() =>
                {
                    StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50"));
                    CameraStatusText.Text = "采集中...";
                    BtnConnect.IsEnabled = false;
                    BtnStartGrab.IsEnabled = false;
                    BtnStopGrab.IsEnabled = true;
                    BtnDisconnect.IsEnabled = false;
                    PreviewPlaceholder.Visibility = Visibility.Collapsed;
                });

            }
            catch (Exception ex)
            {
                Log("开始采集异常: " + ex.Message);
            }
        }

        private void GrabThreadProc()
        {
            MV_FRAME_OUT frameOut = new MV_FRAME_OUT();
            IntPtr framePtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MV_FRAME_OUT)));

            try
            {
                while (_isGrabbing && !_stopEvent.WaitOne(0))
                {
                    int nRet = MV_CC_GetImageBuffer(_deviceHandle, framePtr, 1000);
                    if (nRet == 0)
                    {
                        frameOut = (MV_FRAME_OUT)Marshal.PtrToStructure(framePtr, typeof(MV_FRAME_OUT));

                        // 在UI线程更新图像
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            DisplayImage(frameOut);
                        }));

                        MV_CC_FreeImageBuffer(_deviceHandle, framePtr);
                    }
                    else
                    {
                        // 超时或错误，可以忽略继续
                        Thread.Sleep(10);
                    }
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => Log("采集线程异常: " + ex.Message));
            }
            finally
            {
                Marshal.FreeHGlobal(framePtr);
            }
        }

        private void DisplayImage(MV_FRAME_OUT frameOut)
        {
            try
            {
                if (frameOut.pBufAddr == IntPtr.Zero || frameOut.nWidth == 0 || frameOut.nHeight == 0)
                    return;

                int width = (int)frameOut.nWidth;
                int height = (int)frameOut.nHeight;

                // 根据像素格式创建BitmapSource
                BitmapSource bitmap = null;

                // 尝试创建BitmapSource
                // 注意：实际项目中需要根据相机的像素格式进行转换
                // 常见的格式有：Mono8, Mono16, RGB8, BGR8, BayerGB8 等
                
                try
                {
                    // 假设是BGR8格式 (最常见的彩色相机格式)
                    // Mono8格式可以直接使用
                    bitmap = BitmapSource.Create(
                        width, height,
                        96, 96,
                        PixelFormats.Bgr24,  // 根据实际相机格式调整
                        null,
                        frameOut.pBufAddr,
                        (int)(width * height * 3),  // 根据实际格式调整字节数
                        width * 3  // Stride, 根据实际格式调整
                    );
                    
                    bitmap.Freeze();
                    CameraImage.Source = bitmap;
                }
                catch
                {
                    // 如果格式不匹配，尝试Mono8
                    try
                    {
                        bitmap = BitmapSource.Create(
                            width, height,
                            96, 96,
                            PixelFormats.Gray8,
                            null,
                            frameOut.pBufAddr,
                            width * height,
                            width
                        );
                        bitmap.Freeze();
                        CameraImage.Source = bitmap;
                    }
                    catch
                    {
                        // 忽略显示错误
                    }
                }
            }
            catch (Exception ex)
            {
                // 忽略显示异常
            }
        }

        private void BtnStopGrab_Click(object sender, RoutedEventArgs e)
        {
            StopGrabbing();
        }

        private void StopGrabbing()
        {
            if (!_isGrabbing) return;

            try
            {
                Log("正在停止采集...");

                _isGrabbing = false;
                _stopEvent.Set();

                if (_grabThread != null && _grabThread.IsAlive)
                {
                    _grabThread.Join(1000);
                }

                // 停止采集
                if (_deviceHandle != IntPtr.Zero)
                {
                    MV_CC_StopGrabbing(_deviceHandle);
                }

                Log("图像采集已停止");

                Dispatcher.Invoke(() =>
                {
                    if (_isConnected)
                    {
                        StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50"));
                        CameraStatusText.Text = "相机已连接";
                    }
                    BtnConnect.IsEnabled = false;
                    BtnStartGrab.IsEnabled = true;
                    BtnStopGrab.IsEnabled = false;
                    BtnDisconnect.IsEnabled = true;
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
                // 如果正在采集，先停止
                if (_isGrabbing)
                {
                    StopGrabbing();
                }

                if (_deviceHandle != IntPtr.Zero)
                {
                    Log("正在断开相机连接...");

                    // 关闭设备
                    MV_CC_CloseDevice(_deviceHandle);
                    
                    // 销毁句柄
                    MV_CC_DestroyHandle(_deviceHandle);
                    _deviceHandle = IntPtr.Zero;
                }

                _isConnected = false;
                Log("相机已断开连接");

                Dispatcher.Invoke(() =>
                {
                    StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#666"));
                    CameraStatusText.Text = "相机未连接";
                    CameraImage.Source = null;
                    PreviewPlaceholder.Text = "点击「连接相机」开始预览";
                    PreviewPlaceholder.Visibility = Visibility.Visible;
                    BtnConnect.IsEnabled = true;
                    BtnStartGrab.IsEnabled = false;
                    BtnStopGrab.IsEnabled = false;
                    BtnDisconnect.IsEnabled = false;
                });

            }
            catch (Exception ex)
            {
                Log("断开连接异常: " + ex.Message);
            }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogText.Text = "";
            Log("日志已清空");
        }

        private void Log(string message)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            string logMessage = "[" + time + "] " + message + "\n";

            Dispatcher.Invoke(() =>
            {
                LogText.Text += logMessage;
            });
        }

        /// <summary>
        /// 设置相机信息（由参数配置页面调用）
        /// </summary>
        public void SetCameraInfo(string serialNumber, string ipAddress)
        {
            _cameraSerial = serialNumber;
            _cameraIP = ipAddress;
            
            Dispatcher.Invoke(() =>
            {
                SerialNoText.Text = string.IsNullOrEmpty(serialNumber) ? "未设置" : serialNumber;
            });
        }

        private void UpdateTime()
        {
            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += (s, ev) =>
            {
                CurrentTimeText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            };
            timer.Start();
        }
    }
}
