using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading;
using System.Diagnostics;
using MvCamCtrl.NET;
using System.Runtime.InteropServices;
using System.Text;

namespace WpfApp1
{
    public partial class VisionInspectionPage : UserControl
    {
        private MyCamera m_pCamera = null;
        private bool m_isGrabbing = false;
        private Thread m_hReceiveThread = null;
        private bool m_isThreadRunning = false;
        private string CamSerialStr = "";
        private object m_operateLock = new object();

        public VisionInspectionPage()
        {
            InitializeComponent();
        }

        // 页面加载
        private void VisionInspectionPage_Loaded(object sender, RoutedEventArgs e)
        {
            AddLog("视觉检测页面已加载");
        }

        // 页面卸载
        private void VisionInspectionPage_Unloaded(object sender, RoutedEventArgs e)
        {
            StopGrabbing();
        }

        // 设置相机配置
        public void SetCameraConfig(string serialNo)
        {
            CamSerialStr = serialNo;
            AddLog($"相机序列号: {CamSerialStr}");
        }

        // 添加日志
        private void AddLog(string message)
        {
            try
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    string logEntry = $"[{DateTime.Now:HH:mm:ss}] {message}";
                    LogTextBox.AppendText(logEntry + Environment.NewLine);
                    LogTextBox.ScrollToEnd();
                }));
            }
            catch { }
        }

        // 显示图像
        private void DisplayImage(IntPtr pData, int nWidth, int nHeight, int nPixelType)
        {
            try
            {
                PixelFormat pixelFormat;
                if (nPixelType == (int)MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8)
                {
                    pixelFormat = PixelFormats.Gray8;
                }
                else
                {
                    pixelFormat = PixelFormats.Rgb24;
                }

                BitmapSource bitmap = BitmapSource.Create(
                    nWidth, nHeight, 96, 96,
                    pixelFormat, null, pData, nWidth * nHeight * 3, nWidth * 3);
                bitmap.Freeze();
                CameraDisplay.Source = bitmap;

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
            catch (Exception ex)
            {
                AddLog($"显示图像失败: {ex.Message}");
            }
        }

        // 开始采集按钮
        private void StartGrabButton_Click(object sender, RoutedEventArgs e)
        {
            lock (m_operateLock)
            {
                if (m_isGrabbing)
                {
                    AddLog("相机已在采集中");
                    return;
                }

                // 如果已连接，先断开
                if (m_pCamera != null)
                {
                    AddLog("相机已连接，先断开...");
                    StopCamera();
                }

                AddLog("正在连接相机...");

                // 枚举设备
                MyCamera.MV_CC_DEVICE_INFO_LIST m_pDeviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
                int nRet = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, ref m_pDeviceList);
                if (nRet != MyCamera.MV_OK)
                {
                    AddLog($"枚举设备失败，错误码: 0x{nRet:X8}");
                    return;
                }

                AddLog($"发现 {m_pDeviceList.nDeviceNum} 个设备");

                if (m_pDeviceList.nDeviceNum == 0)
                {
                    AddLog("未发现任何设备");
                    return;
                }

                // 遍历设备找目标相机
                bool deviceFound = false;
                for (int i = 0; i < m_pDeviceList.nDeviceNum; i++)
                {
                    IntPtr pDeviceInfo = Marshal.UnsafeAddrOfPinnedArrayElement(m_pDeviceList.pDeviceInfo, i);
                    MyCamera.MV_CC_DEVICE_INFO device = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));

                    // 获取序列号
                    string deviceSerial = "";
                    if (device.nDeviceType == 0) // GigE
                    {
                        IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(device.SpecialInfo.stGigEInfo, 0);
                        MyCamera.MV_GIGE_DEVICE_INFO gigeInfo = (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(buffer, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                        deviceSerial = Encoding.ASCII.GetString(gigeInfo.chSerialNumber).TrimEnd('\0');
                    }
                    else if (device.nDeviceType == 1) // USB
                    {
                        IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(device.SpecialInfo.stUsb3VInfo, 0);
                        MyCamera.MV_USB3_DEVICE_INFO usbInfo = (MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(buffer, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                        deviceSerial = Encoding.ASCII.GetString(usbInfo.chSerialNumber).TrimEnd('\0');
                    }

                    AddLog($"设备 {i}: 类型={device.nDeviceType}, 序列号={deviceSerial}");

                    // 匹配序列号
                    if (!string.IsNullOrEmpty(deviceSerial) && deviceSerial.Contains(CamSerialStr))
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
                            return;
                        }

                        // 打开设备
                        nRet = m_pCamera.MV_CC_OpenDevice_NET(2, 0);
                        if (nRet != MyCamera.MV_OK)
                        {
                            AddLog($"打开设备失败，错误码: 0x{nRet:X8}");
                            m_pCamera.MV_CC_DestroyDevice_NET();
                            m_pCamera = null;
                            return;
                        }

                        // 开始采集
                        nRet = m_pCamera.MV_CC_StartGrabbing_NET();
                        if (nRet != MyCamera.MV_OK)
                        {
                            AddLog($"开始采集失败，错误码: 0x{nRet:X8}");
                            m_pCamera.MV_CC_CloseDevice_NET();
                            m_pCamera.MV_CC_DestroyDevice_NET();
                            m_pCamera = null;
                            return;
                        }

                        break;
                    }
                }

                if (!deviceFound)
                {
                    AddLog($"未找到序列号为 {CamSerialStr} 的相机");
                    return;
                }

                // 开始接收线程
                m_isThreadRunning = true;
                m_isGrabbing = true;
                m_hReceiveThread = new Thread(ReceiveThread);
                m_hReceiveThread.IsBackground = true;
                m_hReceiveThread.Start();

                AddLog("相机连接成功，开始采集");
                StatusText.Text = "已连接";
                StartGrabButton.IsEnabled = false;
                StopGrabButton.IsEnabled = true;
            }
        }

        // 接收线程
        private void ReceiveThread()
        {
            MyCamera.MV_FRAME_OUT stFrameOut = new MyCamera.MV_FRAME_OUT();
            int nWidth = 0;
            int nHeight = 0;
            int nPixelType = 0;
            IntPtr imageBuffer = IntPtr.Zero;

            while (m_isThreadRunning)
            {
                try
                {
                    if (m_pCamera == null || !m_isGrabbing)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    int nRet = m_pCamera.MV_CC_GetImageBuffer_NET(ref stFrameOut, 100);
                    if (nRet == MyCamera.MV_OK)
                    {
                        nWidth = (int)stFrameOut.nWidth;
                        nHeight = (int)stFrameOut.nHeight;
                        nPixelType = (int)stFrameOut.enPixelType;
                        imageBuffer = stFrameOut.pImageAddr;

                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            DisplayImage(imageBuffer, nWidth, nHeight, nPixelType);
                        }));

                        m_pCamera.MV_CC_FreeImageBuffer_NET(ref stFrameOut);
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"获取图像异常: {ex.Message}");
                }

                Thread.Sleep(10);
            }
        }

        // 停止采集按钮
        private void StopGrabButton_Click(object sender, RoutedEventArgs e)
        {
            lock (m_operateLock)
            {
                if (!m_isGrabbing && m_pCamera == null)
                {
                    AddLog("相机未连接");
                    return;
                }

                AddLog("正在停止采集...");
                StopCamera();
                AddLog("相机已断开");
                StatusText.Text = "已断开";
                StartGrabButton.IsEnabled = true;
                StopGrabButton.IsEnabled = false;
            }
        }

        // 停止相机
        private void StopCamera()
        {
            try
            {
                // 停止接收线程
                m_isThreadRunning = false;
                m_isGrabbing = false;

                if (m_hReceiveThread != null && m_hReceiveThread.IsAlive)
                {
                    m_hReceiveThread.Join(1000);
                    if (m_hReceiveThread.IsAlive)
                    {
                        m_hReceiveThread.Abort();
                    }
                    m_hReceiveThread = null;
                }

                // 停止采集
                if (m_pCamera != null)
                {
                    m_pCamera.MV_CC_StopGrabbing_NET();
                    m_pCamera.MV_CC_CloseDevice_NET();
                    m_pCamera.MV_CC_DestroyDevice_NET();
                    m_pCamera = null;
                }
            }
            catch (Exception ex)
            {
                AddLog($"停止相机异常: {ex.Message}");
            }
        }

        // 保存图像
        private void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_pCamera == null || !m_isGrabbing)
            {
                AddLog("相机未连接或未在采集中");
                return;
            }

            try
            {
                MyCamera.MV_FRAME_OUT stFrameOut = new MyCamera.MV_FRAME_OUT();
                int nRet = m_pCamera.MV_CC_GetImageBuffer_NET(ref stFrameOut, 1000);
                if (nRet == MyCamera.MV_OK)
                {
                    int nWidth = (int)stFrameOut.nWidth;
                    int nHeight = (int)stFrameOut.nHeight;
                    IntPtr pImageBuffer = stFrameOut.pImageAddr;

                    BitmapSource bitmap = BitmapSource.Create(
                        nWidth, nHeight, 96, 96,
                        PixelFormats.Rgb24, null, pImageBuffer, nWidth * nHeight * 3, nWidth * 3);
                    bitmap.Freeze();

                    string fileName = $"image_{DateTime.Now:yyyyMMdd_HHmmss}.bmp";
                    string filePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);

                    using (FileStream fs = new FileStream(filePath, FileMode.Create))
                    {
                        BitmapEncoder encoder = new BmpBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        encoder.Save(fs);
                    }

                    m_pCamera.MV_CC_FreeImageBuffer_NET(ref stFrameOut);
                    AddLog($"图像已保存: {filePath}");
                }
                else
                {
                    AddLog($"获取图像失败，错误码: 0x{nRet:X8}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"保存图像失败: {ex.Message}");
            }
        }

        // 拍照
        private void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            SaveImageButton_Click(sender, e);
        }
    }
}
