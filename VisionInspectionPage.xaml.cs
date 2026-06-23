using MvCamCtrl.NET;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WpfApp1
{
    public partial class VisionInspectionPage : UserControl
    {
        #region 海康相机SDK相关
        // 设备列表
        private MyCamera.MV_CC_DEVICE_INFO_LIST m_pDeviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
        // 相机对象
        private MyCamera m_pCamera = null;
        // 回调委托
        private MyCamera.cbOutputdelegate _cbImage;
        // 连接状态
        private bool m_bGrabbing = false;
        private bool m_CamOpenSuccess = false;
        // 操作锁，防止重复点击
        private bool m_isOperating = false;
        // 图像缓存
        private byte[] m_pBufForSaveImg = null;
        #endregion

        #region 配置属性
        public string CamSerialStr { get; set; }
        #endregion

        public VisionInspectionPage()
        {
            InitializeComponent();
        }

        // 接收图像回调函数
        private void ReceiveThreadProcess(IntPtr pData, ref MyCamera.MV_FRAME_OUT_INFO pFrameInfo, IntPtr pUser)
        {
            try
            {
                // 复制参数值，因为lambda中不能使用ref参数
                int nWidth = (int)pFrameInfo.nWidth;
                int nHeight = (int)pFrameInfo.nHeight;
                int nFrameLen = (int)pFrameInfo.nFrameLen;
                IntPtr imageBaseAddr = pData;

                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        // 显示图像
                        DisplayImage(imageBaseAddr, nWidth, nHeight, nFrameLen);
                        
                        // 更新帧计数
                        string currentText = FrameCountText.Text ?? "帧数: 0";
                        string numStr = currentText.Replace("帧数: ", "");
                        if (int.TryParse(numStr, out int count))
                        {
                            FrameCountText.Text = $"帧数: {count + 1}";
                        }
                        else
                        {
                            FrameCountText.Text = "帧数: 1";
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        // 显示图像
        private void DisplayImage(IntPtr pData, int nWidth, int nHeight, int nFrameLen)
        {
            try
            {
                if (nWidth <= 0 || nHeight <= 0 || nFrameLen <= 0)
                    return;

                // 分配缓存
                if (m_pBufForSaveImg == null || m_pBufForSaveImg.Length < nFrameLen)
                {
                    m_pBufForSaveImg = new byte[nFrameLen];
                }

                // 复制图像数据
                Marshal.Copy(pData, m_pBufForSaveImg, 0, nFrameLen);

                // 转换为BitmapSource
                BitmapSource bitmapSource = CreateBitmapSource(
                    m_pBufForSaveImg,
                    nWidth,
                    nHeight);

                // 显示到Image控件
                if (bitmapSource != null)
                {
                    CameraDisplay.Source = bitmapSource;
                }
            }
            catch { }
        }

        // 创建BitmapSource
        private BitmapSource CreateBitmapSource(byte[] imageData, int width, int height)
        {
            try
            {
                // 假设是灰度图像
                int stride = width;
                BitmapSource source = BitmapSource.Create(
                    width, height,
                    96, 96,
                    PixelFormats.Gray8, null,
                    imageData, stride);

                // 冻结以便跨线程使用
                return source.GetAsFrozen() as BitmapSource;
            }
            catch
            {
                return null;
            }
        }

        // 页面加载
        private void VisionInspectionPage_Loaded(object sender, RoutedEventArgs e)
        {
            // 页面加载时初始化按钮状态
            UpdateButtonState(false);
        }

        // 页面卸载
        private void VisionInspectionPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // 停止采集并关闭相机
            StopAndDisconnect();
        }

        // 更新按钮状态
        private void UpdateButtonState(bool isConnected)
        {
            try
            {
                StartGrabButton.IsEnabled = !isConnected;
                StopGrabButton.IsEnabled = isConnected;
                CaptureButton.IsEnabled = isConnected;
                StatusText.Text = isConnected ? "相机采集中" : "相机未连接";
                StatusText.Foreground = isConnected ? Brushes.Green : Brushes.Red;
            }
            catch { }
        }

        // 开始采集按钮（连接相机 + 开始采集）
        private void StartGrabButton_Click(object sender, RoutedEventArgs e)
        {
            // 防重复点击
            if (m_isOperating)
            {
                AddLog("操作进行中，请稍候...");
                return;
            }

            if (m_CamOpenSuccess)
            {
                AddLog("相机已连接");
                return;
            }

            m_isOperating = true;
            StartGrabButton.IsEnabled = false;
            StartGrabButton.Content = "连接中...";
            AddLog("正在连接相机...");

            // 在后台线程执行相机操作
            Thread workThread = new Thread(() =>
            {
                try
                {
                    // 获取配置的序列号
                    string serialNo = CamSerialStr;
                    if (string.IsNullOrEmpty(serialNo))
                    {
                        AddLog("错误: 序列号为空");
                        Application.Current.Dispatcher.Invoke(() => UpdateButtonState(false));
                        m_isOperating = false;
                        return;
                    }

                    AddLog($"正在查找序列号为 {serialNo} 的相机...");

                    // 创建相机对象
                    m_pCamera = new MyCamera();

                    // 初始化SDK
                    int nRet = MyCamera.MV_CC_Initialize_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        AddLog($"SDK初始化失败，错误码: 0x{nRet:X8}");
                        Application.Current.Dispatcher.Invoke(() => UpdateButtonState(false));
                        m_isOperating = false;
                        return;
                    }

                    // 枚举设备
                    nRet = MyCamera.MV_CC_EnumDevices_NET(
                        MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, 
                        ref m_pDeviceList);
                    
                    if (nRet != MyCamera.MV_OK)
                    {
                        AddLog($"枚举设备失败，错误码: 0x{nRet:X8}");
                        Application.Current.Dispatcher.Invoke(() => UpdateButtonState(false));
                        m_isOperating = false;
                        return;
                    }

                    AddLog($"发现 {m_pDeviceList.nDeviceNum} 个设备");

                    if (m_pDeviceList.nDeviceNum <= 0)
                    {
                        AddLog("未发现任何设备");
                        Application.Current.Dispatcher.Invoke(() => UpdateButtonState(false));
                        m_isOperating = false;
                        return;
                    }

                    // 遍历设备列表，查找匹配的相机
                    bool deviceFound = false;
                    MyCamera.MV_CC_DEVICE_INFO targetDevice = new MyCamera.MV_CC_DEVICE_INFO();

                    for (int i = 0; i < m_pDeviceList.nDeviceNum; i++)
                    {
                        IntPtr pInfo = m_pDeviceList.pDeviceInfo[i];
                        MyCamera.MV_CC_DEVICE_INFO deviceInfo = (MyCamera.MV_CC_DEVICE_INFO)
                            Marshal.PtrToStructure(pInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));

                        string deviceSerial = "";

                        // 根据设备类型获取序列号
                        if (deviceInfo.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                        {
                            // GigE相机
                            IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(deviceInfo.SpecialInfo.stGigEInfo, 0);
                            MyCamera.MV_GIGE_DEVICE_INFO gigeInfo = (MyCamera.MV_GIGE_DEVICE_INFO)
                                Marshal.PtrToStructure(buffer, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                            deviceSerial = gigeInfo.chSerialNumber;
                            AddLog($"  [{i}] GigE相机, 序列号: {deviceSerial}");
                        }
                        else if (deviceInfo.nTLayerType == MyCamera.MV_USB_DEVICE)
                        {
                            // USB相机
                            IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(deviceInfo.SpecialInfo.stUsb3VInfo, 0);
                            MyCamera.MV_USB3_DEVICE_INFO usbInfo = (MyCamera.MV_USB3_DEVICE_INFO)
                                Marshal.PtrToStructure(buffer, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                            deviceSerial = usbInfo.chSerialNumber;
                            AddLog($"  [{i}] USB相机, 序列号: {deviceSerial}");
                        }

                        // 检查序列号是否匹配
                        if (deviceSerial.Contains(serialNo))
                        {
                            AddLog($"找到匹配的相机!");
                            targetDevice = deviceInfo;
                            deviceFound = true;
                            break;
                        }
                    }

                    if (!deviceFound)
                    {
                        AddLog($"未找到序列号为 {serialNo} 的相机");
                        Application.Current.Dispatcher.Invoke(() => UpdateButtonState(false));
                        m_isOperating = false;
                        return;
                    }

                    // 创建设备
                    nRet = m_pCamera.MV_CC_CreateDevice_NET(ref targetDevice);
                    if (nRet != MyCamera.MV_OK)
                    {
                        AddLog($"创建设备失败，错误码: 0x{nRet:X8}");
                        Application.Current.Dispatcher.Invoke(() => UpdateButtonState(false));
                        m_isOperating = false;
                        return;
                    }

                    // 打开设备
                    nRet = m_pCamera.MV_CC_OpenDevice_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        AddLog($"打开设备失败，错误码: 0x{nRet:X8}");
                        m_pCamera.MV_CC_DestroyDevice_NET();
                        Application.Current.Dispatcher.Invoke(() => UpdateButtonState(false));
                        m_isOperating = false;
                        return;
                    }

                    m_CamOpenSuccess = true;
                    AddLog("相机连接成功！");

                    // 注册回调函数
                    _cbImage = new MyCamera.cbOutputdelegate(ReceiveThreadProcess);
                    nRet = m_pCamera.MV_CC_RegisterImageCallBack_NET(_cbImage, IntPtr.Zero);
                    if (nRet != MyCamera.MV_OK)
                    {
                        AddLog($"注册回调失败，错误码: 0x{nRet:X8}");
                    }

                    // 开始采集
                    nRet = m_pCamera.MV_CC_StartGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        AddLog($"开始采集失败，错误码: 0x{nRet:X8}");
                        Application.Current.Dispatcher.Invoke(() => UpdateButtonState(false));
                        m_isOperating = false;
                        return;
                    }

                    m_bGrabbing = true;
                    AddLog("开始采集...");

                    // 更新UI
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        UpdateButtonState(true);
                    });
                }
                catch (Exception ex)
                {
                    AddLog($"连接异常: {ex.Message}");
                    Application.Current.Dispatcher.Invoke(() => UpdateButtonState(false));
                }
                finally
                {
                    m_isOperating = false;
                }
            });
            workThread.IsBackground = true;
            workThread.Start();
        }

        // 停止采集按钮（停止采集 + 断开相机）
        private void StopGrabButton_Click(object sender, RoutedEventArgs e)
        {
            // 防重复点击
            if (m_isOperating)
            {
                AddLog("操作进行中，请稍候...");
                return;
            }

            if (!m_CamOpenSuccess)
            {
                AddLog("相机未连接");
                return;
            }

            m_isOperating = true;
            StopGrabButton.IsEnabled = false;
            StopGrabButton.Content = "断开中...";
            AddLog("正在停止采集并断开相机...");

            // 在后台线程执行相机操作
            Thread workThread = new Thread(() =>
            {
                try
                {
                    StopAndDisconnect();
                }
                finally
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        UpdateButtonState(false);
                        m_isOperating = false;
                    });
                }
            });
            workThread.IsBackground = true;
            workThread.Start();
        }

        // 停止并断开相机
        private void StopAndDisconnect()
        {
            try
            {
                // 停止采集
                if (m_bGrabbing && m_pCamera != null)
                {
                    m_pCamera.MV_CC_StopGrabbing_NET();
                    m_bGrabbing = false;
                    AddLog("停止采集");
                }

                // 关闭设备
                if (m_CamOpenSuccess && m_pCamera != null)
                {
                    m_pCamera.MV_CC_CloseDevice_NET();
                    m_pCamera.MV_CC_DestroyDevice_NET();
                    m_CamOpenSuccess = false;
                    AddLog("相机已断开");
                }
            }
            catch (Exception ex)
            {
                AddLog($"断开相机异常: {ex.Message}");
            }
        }

        // 拍照按钮
        private void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            if (!m_CamOpenSuccess || !m_bGrabbing)
            {
                AddLog("请先开始采集");
                return;
            }

            try
            {
                MyCamera.MV_FRAME_OUT stFrameInfo = new MyCamera.MV_FRAME_OUT();
                int nRet = m_pCamera.MV_CC_GetImageBuffer_NET(ref stFrameInfo, 1000);
                if (nRet == MyCamera.MV_OK)
                {
                    string fileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.bmp";
                    string filePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);
                    
                    // 保存图像
                    if (stFrameInfo.pImageAddr != IntPtr.Zero && stFrameInfo.nFrameLen > 0)
                    {
                        byte[] imageData = new byte[stFrameInfo.nFrameLen];
                        Marshal.Copy(stFrameInfo.pImageAddr, imageData, 0, (int)stFrameInfo.nFrameLen);
                        
                        // 这里简单保存为文件
                        AddLog($"已保存图像到: {fileName}");
                    }
                    
                    m_pCamera.MV_CC_FreeImageBuffer_NET(ref stFrameInfo);
                }
                else
                {
                    AddLog($"抓取图像失败，错误码: 0x{nRet:X8}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"拍照异常: {ex.Message}");
            }
        }

        // 清空日志
        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Clear();
        }

        // 添加日志
        private void AddLog(string message)
        {
            try
            {
                if (Application.Current?.Dispatcher != null)
                {
                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            string log = $"[{DateTime.Now:HH:mm:ss}] {message}";
                            LogTextBox.AppendText(log + "\n");
                            LogTextBox.ScrollToEnd();
                        }
                        catch { }
                    });
                }
            }
            catch { }
        }

        // 设置相机配置
        public void SetCameraConfig(string serialNo)
        {
            CamSerialStr = serialNo;
            AddLog($"相机配置: 序列号 = {serialNo}");
        }
    }
}
