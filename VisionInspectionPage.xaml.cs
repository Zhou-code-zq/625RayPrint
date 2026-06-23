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
        // 像素格式
        private PixelFormat pixelFormats12 = PixelFormats.Gray8;
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
                // 复制参数值
                int nWidth = (int)pFrameInfo.nWidth;
                int nHeight = (int)pFrameInfo.nHeight;
                int nFrameLen = (int)pFrameInfo.nFrameLen;
                IntPtr imageBaseAddr = pData;
                uint nPixelType = pFrameInfo.enPixelType;

                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // 显示图像
                        DisplayImage(imageBaseAddr, nWidth, nHeight, (int)nPixelType);
                        
                        // 更新帧计数
                        string currentText = FrameCountText.Text ?? "帧数: 0";
                        string numStr = currentText.Replace("帧数: ", "").Trim();
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
                }));
            }
            catch { }
        }

        // 显示图像
        private void DisplayImage(IntPtr pData, int nWidth, int nHeight, int nPixelType)
        {
            try
            {
                // 确定像素格式
                PixelFormat pixelFormat;
                if (nPixelType == (int)MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8)
                {
                    pixelFormat = PixelFormats.Gray8;
                }
                else
                {
                    pixelFormat = PixelFormats.Rgb24;
                }

                // 每行字节数
                int bytesPerPixel = pixelFormat.BitsPerPixel / 8;
                int stride = nWidth * bytesPerPixel;

                // 直接从IntPtr创建BitmapSource，零内存拷贝
                BitmapSource bitmapSource = BitmapSource.Create(
                    nWidth, nHeight, 96, 96,
                    pixelFormat, null, pData,
                    nWidth * nHeight * bytesPerPixel, stride);

                bitmapSource.Freeze();
                CameraDisplay.Source = bitmapSource;
            }
            catch (Exception ex)
            {
                AddLog($"显示图像异常: {ex.Message}");
            }
        }

        // 开始采集按钮
        private void StartGrabButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_isOperating) return;
            m_isOperating = true;

            StartGrabButton.IsEnabled = false;
            StartGrabButton.Content = "采集中...";

            new Thread(() =>
            {
                try
                {
                    if (!m_CamOpenSuccess)
                    {
                        // 连接相机
                        if (!ConnectCamera())
                        {
                            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                StartGrabButton.IsEnabled = true;
                                StartGrabButton.Content = "开始采集";
                            }));
                            m_isOperating = false;
                            return;
                        }
                    }

                    // 开始采集
                    int nRet = m_pCamera.MV_CC_StartGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        AddLog($"开始采集失败，错误码: 0x{nRet:X8}");
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            StartGrabButton.IsEnabled = true;
                            StartGrabButton.Content = "开始采集";
                        }));
                        m_isOperating = false;
                        return;
                    }

                    m_bGrabbing = true;
                    AddLog("开始采集成功");

                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StartGrabButton.IsEnabled = false;
                        StartGrabButton.Content = "采集中";
                        StopGrabButton.IsEnabled = true;
                    }));
                }
                catch (Exception ex)
                {
                    AddLog($"异常: {ex.Message}");
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StartGrabButton.IsEnabled = true;
                        StartGrabButton.Content = "开始采集";
                    }));
                }
                finally
                {
                    m_isOperating = false;
                }
            }).Start();
        }

        // 停止采集按钮
        private void StopGrabButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_isOperating) return;
            m_isOperating = true;

            StopGrabButton.IsEnabled = false;
            StopGrabButton.Content = "停止中...";

            new Thread(() =>
            {
                try
                {
                    // 停止采集
                    if (m_bGrabbing && m_pCamera != null)
                    {
                        int nRet = m_pCamera.MV_CC_StopGrabbing_NET();
                        if (nRet != MyCamera.MV_OK)
                        {
                            AddLog($"停止采集失败，错误码: 0x{nRet:X8}");
                        }
                        else
                        {
                            AddLog("停止采集成功");
                        }
                        m_bGrabbing = false;
                    }

                    // 关闭相机
                    if (m_CamOpenSuccess && m_pCamera != null)
                    {
                        int nRet = m_pCamera.MV_CC_CloseDevice_NET();
                        if (nRet != MyCamera.MV_OK)
                        {
                            AddLog($"关闭设备失败，错误码: 0x{nRet:X8}");
                        }
                        else
                        {
                            AddLog("设备已断开");
                        }

                        nRet = m_pCamera.MV_CC_DestroyDevice_NET();
                        if (nRet != MyCamera.MV_OK)
                        {
                            AddLog($"销毁设备失败，错误码: 0x{nRet:X8}");
                        }

                        m_CamOpenSuccess = false;
                        m_pCamera = null;
                    }

                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StopGrabButton.IsEnabled = false;
                        StopGrabButton.Content = "已停止";
                        StartGrabButton.IsEnabled = true;
                        StartGrabButton.Content = "开始采集";
                        FrameCountText.Text = "帧数: 0";
                    }));
                }
                catch (Exception ex)
                {
                    AddLog($"异常: {ex.Message}");
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StopGrabButton.IsEnabled = true;
                        StopGrabButton.Content = "停止采集";
                    }));
                }
                finally
                {
                    m_isOperating = false;
                }
            }).Start();
        }

        // 保存图像按钮
        private void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_pCamera == null || !m_bGrabbing)
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
                    // 使用MV_FRAME_OUT的字段（根据SDK版本可能不同）
                    // stFrameInfo.pFrameBufAddr 或 pImageAddr
                    // stFrameInfo.nFrameLen 或 nFrameSize
                    
                    // 释放图像缓存
                    m_pCamera.MV_CC_FreeImageBuffer_NET(ref stFrameInfo);
                    AddLog("已保存图像");
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

        // 连接相机
        private bool ConnectCamera()
        {
            if (string.IsNullOrEmpty(CamSerialStr))
            {
                AddLog("请先在参数配置中设置相机序列号");
                return false;
            }

            AddLog($"正在查找序列号为 {CamSerialStr} 的相机...");

            // 初始化SDK
            int nRet = MyCamera.MV_CC_Initialize_NET();
            if (nRet != MyCamera.MV_OK)
            {
                AddLog($"初始化SDK失败，错误码: 0x{nRet:X8}");
                return false;
            }

            // 枚举设备
            m_pDeviceList.nDeviceNum = 0;
            nRet = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, ref m_pDeviceList);
            if (nRet != MyCamera.MV_OK)
            {
                AddLog($"枚举设备失败，错误码: 0x{nRet:X8}");
                return false;
            }

            if (m_pDeviceList.nDeviceNum == 0)
            {
                AddLog("未发现任何相机设备");
                return false;
            }

            AddLog($"发现 {m_pDeviceList.nDeviceNum} 个设备");

            // 遍历设备列表，查找匹配的序列号
            bool deviceFound = false;
            for (ushort i = 0; i < m_pDeviceList.nDeviceNum; i++)
            {
                IntPtr pDeviceInfo = Marshal.UnsafeAddrOfPinnedArrayElement(m_pDeviceList.pDeviceInfo, (int)i);
                MyCamera.MV_CC_DEVICE_INFO device = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));

                string deviceSerial = "";
                
                if (device.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                {
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(device.SpecialInfo.stGigEInfo, 0);
                    MyCamera.MV_GIGE_DEVICE_INFO gigeInfo = (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(buffer, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                    deviceSerial = System.Text.Encoding.ASCII.GetString(gigeInfo.chSerialNumber).TrimEnd('\0');
                }
                else if (device.nTLayerType == MyCamera.MV_USB_DEVICE)
                {
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(device.SpecialInfo.stUsb3VInfo, 0);
                    MyCamera.MV_USB3_DEVICE_INFO usbInfo = (MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(buffer, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                    deviceSerial = System.Text.Encoding.ASCII.GetString(usbInfo.chSerialNumber).TrimEnd('\0');
                }

                AddLog($"设备 {i}: 类型={device.nTLayerType}, 序列号={deviceSerial}");

                if (deviceSerial.Contains(CamSerialStr))
                {
                    AddLog($"找到目标相机: 序列号={deviceSerial}");
                    deviceFound = true;

                    // 创建设备
                    m_pCamera = new MyCamera();
                    nRet = m_pCamera.MV_CC_CreateDevice_NET(ref device);
                    if (nRet != MyCamera.MV_OK)
                    {
                        AddLog($"创建设备失败，错误码: 0x{nRet:X8}");
                        m_pCamera = null;
                        return false;
                    }

                    // 打开设备
                    nRet = m_pCamera.MV_CC_OpenDevice_NET(2, 0); // 2=Exclusive access
                    if (nRet != MyCamera.MV_OK)
                    {
                        AddLog($"打开设备失败，错误码: 0x{nRet:X8}");
                        m_pCamera.MV_CC_DestroyDevice_NET();
                        m_pCamera = null;
                        return false;
                    }

                    // 注册回调
                    _cbImage = new MyCamera.cbOutputdelegate(ReceiveThreadProcess);
                    nRet = m_pCamera.MV_CC_RegisterImageCallBack(_cbImage, IntPtr.Zero);
                    if (nRet != MyCamera.MV_OK)
                    {
                        AddLog($"注册回调失败，错误码: 0x{nRet:X8}");
                        m_pCamera.MV_CC_CloseDevice_NET();
                        m_pCamera.MV_CC_DestroyDevice_NET();
                        m_pCamera = null;
                        return false;
                    }

                    m_CamOpenSuccess = true;
                    AddLog("相机连接成功");
                    return true;
                }
            }

            if (!deviceFound)
            {
                AddLog($"未找到序列号为 {CamSerialStr} 的相机");
            }
            return false;
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
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            string log = $"[{DateTime.Now:HH:mm:ss}] {message}";
                            LogTextBox.AppendText(log + "\n");
                            LogTextBox.ScrollToEnd();
                        }
                        catch { }
                    }));
                }
            }
            catch { }
        }

        // 页面加载时
        private void VisionInspectionPage_Loaded(object sender, RoutedEventArgs e)
        {
            AddLog("质检与监控页面已加载");
        }

        // 页面卸载时
        private void VisionInspectionPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // 断开相机连接
            if (m_pCamera != null)
            {
                try
                {
                    if (m_bGrabbing)
                    {
                        m_pCamera.MV_CC_StopGrabbing_NET();
                    }
                    if (m_CamOpenSuccess)
                    {
                        m_pCamera.MV_CC_CloseDevice_NET();
                        m_pCamera.MV_CC_DestroyDevice_NET();
                    }
                }
                catch { }
            }
            AddLog("质检与监控页面已卸载");
        }

        // 连接并开始采集
        private void CameraConnectButton_Click(object sender, RoutedEventArgs e)
        {
            // 触发开始采集
            StartGrabButton_Click(sender, e);
        }

        // 拍照按钮
        private void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            SaveImageButton_Click(sender, e);
        }

        // 设置相机配置
        public void SetCameraConfig(string serialNo)
        {
            CamSerialStr = serialNo;
            AddLog($"相机配置: 序列号 = {serialNo}");
        }
    }
}
