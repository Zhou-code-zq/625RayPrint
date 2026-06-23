using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MvCamCtrl.NET;

namespace WpfApp1
{
    public partial class VisionInspectionPage : Page
    {
        private MyCamera m_pDevice;
        private DispatcherTimer m_timer;
        private Thread m_hReceiveThread;
        private bool m_isGrabbing = false;
        private uint m_nFrameCount = 0;
        private string m_deviceSerial = "";
        private uint m_deviceType = 0;

        public VisionInspectionPage()
        {
            InitializeComponent();
            InitializeCamera();
        }

        private void InitializeCamera()
        {
            m_timer = new DispatcherTimer();
            m_timer.Interval = TimeSpan.FromMilliseconds(100);
            m_timer.Tick += Timer_Tick;
        }

        public void SetCameraConfig(string serialNo, uint deviceType)
        {
            m_deviceSerial = serialNo;
            m_deviceType = deviceType;
            LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 配置已更新: 序列号={serialNo}, 类型={deviceType}\n");
            LogTextBox.ScrollToEnd();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (m_pDevice != null && m_isGrabbing)
            {
                try
                {
                    MyCamera.MV_FRAME_OUT stFrameOut = new MyCamera.MV_FRAME_OUT();
                    int nRet = m_pDevice.MV_CC_GetImageBuffer_NET(ref stFrameOut, 100);
                    if (nRet == MyCamera.MV_OK)
                    {
                        DisplayImage(stFrameOut);
                        m_nFrameCount++;
                        FrameCountText.Text = m_nFrameCount.ToString();
                        m_pDevice.MV_CC_FreeImageBuffer_NET(ref stFrameOut);
                    }
                }
                catch (Exception ex)
                {
                    LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 获取图像失败: {ex.Message}\n");
                    LogTextBox.ScrollToEnd();
                }
            }
        }

        private void DisplayImage(MyCamera.MV_FRAME_OUT stFrameOut)
        {
            try
            {
                IntPtr pData = stFrameOut.pImageAddr;
                int nWidth = stFrameOut.nWidth;
                int nHeight = stFrameOut.nHeight;
                int nFrameLen = stFrameOut.nFrameLen;
                MyCamera.MvGvspPixelType enPixelType = stFrameOut.enPixelType;

                // 创建Bitmap图像
                Int32Rect sourceRect = new Int32Rect(0, 0, nWidth, nHeight);
                BitmapSource bitmapSource = BitmapSource.Create(
                    nWidth, nHeight, 96, 96,
                    System.Windows.Media.PixelFormats.Bgr24,
                    null, pData, nWidth * nHeight * 3, nWidth * 3);
                bitmapSource.Freeze();
                CameraDisplay.Source = bitmapSource;
            }
            catch (Exception ex)
            {
                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 显示图像失败: {ex.Message}\n");
                LogTextBox.ScrollToEnd();
            }
        }

        private void StartCameraButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_isGrabbing)
            {
                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 相机已在采集\n");
                LogTextBox.ScrollToEnd();
                return;
            }

            new Thread(() =>
            {
                try
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StartCameraButton.IsEnabled = false;
                        StopCameraButton.IsEnabled = false;
                    }));

                    // 初始化SDK
                    int nRet = MyCamera.MV_CC_Initialize_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] SDK初始化失败，错误码: 0x{nRet:X}\n");
                        LogTextBox.ScrollToEnd();
                        return;
                    }

                    // 枚举设备
                    MyCamera.MV_CC_DEVICE_INFO_LIST m_pDeviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
                    uint nDeviceType = MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE;
                    nRet = MyCamera.MV_CC_EnumDevices_NET(nDeviceType, ref m_pDeviceList);
                    if (nRet != MyCamera.MV_OK)
                    {
                        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 枚举设备失败，错误码: 0x{nRet:X}\n");
                        LogTextBox.ScrollToEnd();
                        return;
                    }

                    if (m_pDeviceList.nDeviceNum == 0)
                    {
                        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 未发现任何设备\n");
                        LogTextBox.ScrollToEnd();
                        return;
                    }

                    LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 发现 {m_pDeviceList.nDeviceNum} 个设备\n");
                    LogTextBox.ScrollToEnd();

                    // 创建设备
                    m_pDevice = new MyCamera();
                    IntPtr pDeviceInfo = IntPtr.Zero;
                    for (int i = 0; i < m_pDeviceList.nDeviceNum; i++)
                    {
                        pDeviceInfo = Marshal.AddrOfPinnedArrayElement(m_pDeviceList.pDeviceInfo, i);
                        MyCamera.MV_CC_DEVICE_INFO deviceInfo = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));

                        string serialNo = "";
                        if (deviceInfo.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                        {
                            IntPtr pGigEInfo = Marshal.AddrOfPinnedArrayElement(deviceInfo.SpecialInfo.stGigEInfo, 0);
                            MyCamera.MV_GIGE_DEVICE_INFO gigeInfo = (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(pGigEInfo, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                            serialNo = System.Text.Encoding.ASCII.GetString(gigeInfo.chSerialNumber).TrimEnd('\0');
                        }
                        else if (deviceInfo.nTLayerType == MyCamera.MV_USB_DEVICE)
                        {
                            IntPtr pUsbInfo = Marshal.AddrOfPinnedArrayElement(deviceInfo.SpecialInfo.stUsb3VInfo, 0);
                            MyCamera.MV_USB3_DEVICE_INFO usbInfo = (MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(pUsbInfo, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                            serialNo = System.Text.Encoding.ASCII.GetString(usbInfo.chSerialNumber).TrimEnd('\0');
                        }

                        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 设备{i}: 序列号={serialNo}\n");
                        LogTextBox.ScrollToEnd();

                        if (serialNo == m_deviceSerial || string.IsNullOrEmpty(m_deviceSerial))
                        {
                            nRet = m_pDevice.MV_CC_CreateDevice_NET(ref deviceInfo);
                            if (nRet != MyCamera.MV_OK)
                            {
                                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 创建设备失败，错误码: 0x{nRet:X}\n");
                                LogTextBox.ScrollToEnd();
                                continue;
                            }
                            break;
                        }
                    }

                    if (nRet != MyCamera.MV_OK)
                    {
                        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 未找到匹配的设备\n");
                        LogTextBox.ScrollToEnd();
                        return;
                    }

                    // 打开设备
                    nRet = m_pDevice.MV_CC_OpenDevice_NET(MyCamera.MV_ACCESS_Exclusive, 0);
                    if (nRet != MyCamera.MV_OK)
                    {
                        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 打开设备失败，错误码: 0x{nRet:X}\n");
                        LogTextBox.ScrollToEnd();
                        return;
                    }

                    // 开始采集
                    nRet = m_pDevice.MV_CC_StartGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 开始采集失败，错误码: 0x{nRet:X}\n");
                        LogTextBox.ScrollToEnd();
                        return;
                    }

                    m_isGrabbing = true;
                    m_nFrameCount = 0;
                    m_timer.Start();

                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StartCameraButton.IsEnabled = false;
                        StopCameraButton.IsEnabled = true;
                        StartCameraButton.Content = "已连接";
                    }));

                    LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 相机连接成功，开始采集\n");
                    LogTextBox.ScrollToEnd();
                }
                catch (Exception ex)
                {
                    LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 错误: {ex.Message}\n");
                    LogTextBox.ScrollToEnd();
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StartCameraButton.IsEnabled = true;
                    }));
                }
            }).Start();
        }

        private void StopCameraButton_Click(object sender, RoutedEventArgs e)
        {
            if (!m_isGrabbing)
            {
                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 相机已停止\n");
                LogTextBox.ScrollToEnd();
                return;
            }

            new Thread(() =>
            {
                try
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StopCameraButton.IsEnabled = false;
                    }));

                    m_isGrabbing = false;
                    m_timer.Stop();

                    if (m_pDevice != null)
                    {
                        m_pDevice.MV_CC_StopGrabbing_NET();
                        m_pDevice.MV_CC_CloseDevice_NET();
                        m_pDevice.MV_CC_DestroyDevice_NET();
                        m_pDevice = null;
                    }

                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StartCameraButton.IsEnabled = true;
                        StopCameraButton.IsEnabled = false;
                        StartCameraButton.Content = "开始采集";
                        CameraDisplay.Source = null;
                    }));

                    LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 相机已停止\n");
                    LogTextBox.ScrollToEnd();
                }
                catch (Exception ex)
                {
                    LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 停止失败: {ex.Message}\n");
                    LogTextBox.ScrollToEnd();
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StopCameraButton.IsEnabled = true;
                    }));
                }
            }).Start();
        }

        private void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (!m_isGrabbing || m_pDevice == null)
            {
                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 请先连接相机\n");
                LogTextBox.ScrollToEnd();
                return;
            }

            try
            {
                MyCamera.MV_FRAME_OUT stFrameOut = new MyCamera.MV_FRAME_OUT();
                int nRet = m_pDevice.MV_CC_GetImageBuffer_NET(ref stFrameOut, 1000);
                if (nRet == MyCamera.MV_OK)
                {
                    string savePath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        $"Image_{DateTime.Now:yyyyMMdd_HHmmss}.bmp");

                    // 保存为BMP文件
                    IntPtr pData = stFrameOut.pImageAddr;
                    int nWidth = stFrameOut.nWidth;
                    int nHeight = stFrameOut.nHeight;
                    int nImageLen = stFrameOut.nFrameLen;

                    byte[] imageData = new byte[nImageLen];
                    Marshal.Copy(pData, imageData, 0, (int)nImageLen);

                    using (FileStream fs = new FileStream(savePath, FileMode.Create))
                    {
                        // BMP文件头
                        byte[] header = new byte[54];
                        header[0] = 0x42; header[1] = 0x4D; // "BM"
                        BitConverter.GetBytes(54 + nImageLen).CopyTo(header, 2);
                        BitConverter.GetBytes(54).CopyTo(header, 10);
                        BitConverter.GetBytes(54).CopyTo(header, 14);
                        BitConverter.GetBytes(nWidth).CopyTo(header, 18);
                        BitConverter.GetBytes(nHeight).CopyTo(header, 22);
                        header[26] = 1; header[27] = 0; // planes
                        header[28] = 24; // bits per pixel
                        fs.Write(header, 0, 54);
                        fs.Write(imageData, 0, (int)nImageLen);
                    }

                    m_pDevice.MV_CC_FreeImageBuffer_NET(ref stFrameOut);
                    LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 图像已保存: {savePath}\n");
                    LogTextBox.ScrollToEnd();
                }
                else
                {
                    LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 获取图像失败\n");
                    LogTextBox.ScrollToEnd();
                }
            }
            catch (Exception ex)
            {
                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 保存失败: {ex.Message}\n");
                LogTextBox.ScrollToEnd();
            }
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Clear();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            StopCameraButton_Click(null, null);
        }
    }
}
