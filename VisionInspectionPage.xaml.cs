using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using MvCamCtrl.NET;

namespace WpfApp1
{
    public partial class VisionInspectionPage : UserControl
    {
        private MyCamera m_pCamera;
        private bool m_bGrabbing = false;
        private uint m_nFrameCount = 0;
        private Thread m_hReceiveThread = null;
        private ManualResetEvent m_hReceiveThreadStarted = new ManualResetEvent(false);
        private static readonly object m_lockObj = new object();

        // 相机配置
        private string m_strCameraSerial = "";
        private uint m_nDeviceType = 0;

        public VisionInspectionPage()
        {
            InitializeComponent();
        }

        public void SetCameraConfig(string serialNo, uint deviceType)
        {
            m_strCameraSerial = serialNo;
            m_nDeviceType = deviceType;
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = $"状态: 已配置 (序列号: {serialNo})";
            });
        }

        private void CameraConnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LogTextBox.Clear();
                AddLog("开始连接相机...");

                // 枚举设备
                MyCamera.MV_CC_DEVICE_INFO_LIST m_pDeviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
                int nRet = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, ref m_pDeviceList);
                if (MyCamera.MV_OK != nRet)
                {
                    AddLog($"枚举设备失败! nRet = 0x{nRet:X}");
                    return;
                }

                AddLog($"发现 {m_pDeviceList.nDeviceNum} 个设备");

                if (m_pDeviceList.nDeviceNum == 0)
                {
                    AddLog("未发现任何设备");
                    return;
                }

                IntPtr pDeviceInfo = IntPtr.Zero;
                MyCamera.MV_CC_DEVICE_INFO targetDevice = new MyCamera.MV_CC_DEVICE_INFO();
                bool bFound = false;

                for (uint i = 0; i < m_pDeviceList.nDeviceNum; i++)
                {
                    pDeviceInfo = Marshal.UnsafeAddrOfPinnedArrayElement(m_pDeviceList.pDeviceInfo, (int)i);
                    MyCamera.MV_CC_DEVICE_INFO device = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));

                    string strSerial = "";
                    if (device.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                    {
                        IntPtr pGigEInfo = Marshal.UnsafeAddrOfPinnedArrayElement(device.SpecialInfo.stGigEInfo, 0);
                        MyCamera.MV_GIGE_DEVICE_INFO gigeInfo = (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(pGigEInfo, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                        strSerial = System.Text.Encoding.ASCII.GetString(gigeInfo.chSerialNumber).TrimEnd('\0');
                    }
                    else if (device.nTLayerType == MyCamera.MV_USB_DEVICE)
                    {
                        IntPtr pUsbInfo = Marshal.UnsafeAddrOfPinnedArrayElement(device.SpecialInfo.stUsb3VInfo, 0);
                        MyCamera.MV_USB3_DEVICE_INFO usbInfo = (MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(pUsbInfo, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                        strSerial = System.Text.Encoding.ASCII.GetString(usbInfo.chSerialNumber).TrimEnd('\0');
                    }

                    AddLog($"设备 {i}: 类型={device.nTLayerType}, 序列号={strSerial}");

                    if (!string.IsNullOrEmpty(m_strCameraSerial) && strSerial == m_strCameraSerial)
                    {
                        targetDevice = device;
                        bFound = true;
                        AddLog($"找到目标相机: {strSerial}");
                        break;
                    }
                }

                if (!bFound && m_pDeviceList.nDeviceNum > 0)
                {
                    pDeviceInfo = Marshal.UnsafeAddrOfPinnedArrayElement(m_pDeviceList.pDeviceInfo, 0);
                    targetDevice = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));
                    bFound = true;
                    AddLog("使用第一个可用设备");
                }

                if (!bFound)
                {
                    AddLog($"未找到序列号为 {m_strCameraSerial} 的相机");
                    return;
                }

                // 创建设备
                m_pCamera = new MyCamera();
                nRet = m_pCamera.MV_CC_CreateDevice_NET(ref targetDevice);
                if (MyCamera.MV_OK != nRet)
                {
                    AddLog($"创建设备失败! nRet = 0x{nRet:X}");
                    return;
                }

                // 打开设备
                nRet = m_pCamera.MV_CC_OpenDevice_NET(MyCamera.MV_ACCESS_Exclusive, 0);
                if (MyCamera.MV_OK != nRet)
                {
                    AddLog($"打开设备失败! nRet = 0x{nRet:X}");
                    m_pCamera.MV_CC_DestroyDevice_NET();
                    return;
                }

                AddLog("相机连接成功!");
                Dispatcher.Invoke(() =>
                {
                    CameraConnectButton.IsEnabled = false;
                    StartGrabButton.IsEnabled = true;
                    StopGrabButton.IsEnabled = true;
                    StatusText.Text = "状态: 已连接";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(78, 205, 196));
                });
            }
            catch (Exception ex)
            {
                AddLog($"连接异常: {ex.Message}");
            }
        }

        private void StartGrabButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_pCamera == null)
            {
                AddLog("请先连接相机");
                return;
            }

            try
            {
                int nRet = m_pCamera.MV_CC_StartGrabbing_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    AddLog($"开始采集失败! nRet = 0x{nRet:X}");
                    return;
                }

                m_bGrabbing = true;
                m_nFrameCount = 0;
                Dispatcher.Invoke(() =>
                {
                    FrameCountText.Text = "帧数: 0";
                    StartGrabButton.IsEnabled = false;
                    StopGrabButton.IsEnabled = true;
                });

                m_hReceiveThread = new Thread(ReceiveThread);
                m_hReceiveThread.Start();
                m_hReceiveThreadStarted.WaitOne();

                AddLog("开始采集图像...");
            }
            catch (Exception ex)
            {
                AddLog($"开始采集异常: {ex.Message}");
            }
        }

        private void StopGrabButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                m_bGrabbing = false;

                if (m_pCamera != null)
                {
                    m_pCamera.MV_CC_StopGrabbing_NET();
                }

                if (m_hReceiveThread != null && m_hReceiveThread.IsAlive)
                {
                    m_hReceiveThread.Join(1000);
                }

                Dispatcher.Invoke(() =>
                {
                    StartGrabButton.IsEnabled = true;
                    StopGrabButton.IsEnabled = false;
                });

                AddLog("停止采集");
            }
            catch (Exception ex)
            {
                AddLog($"停止采集异常: {ex.Message}");
            }
        }

        private void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_pCamera == null || !m_bGrabbing)
            {
                AddLog("请先连接相机并开始采集");
                return;
            }

            lock (m_lockObj)
            {
                MyCamera.MV_FRAME_OUT stFrameOut = new MyCamera.MV_FRAME_OUT();
                int nRet = m_pCamera.MV_CC_GetImageBuffer_NET(ref stFrameOut, 1000);
                if (nRet == MyCamera.MV_OK)
                {
                    string strPath = Directory.GetCurrentDirectory() + $"\\Images\\{DateTime.Now:yyyyMMdd_HHmmss_fff}.bmp";
                    Directory.CreateDirectory(Path.GetDirectoryName(strPath));

                    uint nWidth = stFrameOut.stFrameInfo.nWidth;
                    uint nHeight = stFrameOut.stFrameInfo.nHeight;
                    uint nFrameLen = stFrameOut.stFrameInfo.nFrameLen;
                    MyCamera.MvGvspPixelType enPixelType = stFrameOut.stFrameInfo.enPixelType;

                    nRet = m_pCamera.MV_CC_SaveImageToFile_NET(stFrameOut.stFrameOut.pImageAddr, nWidth, nHeight, nFrameLen, enPixelType, strPath);

                    if (MyCamera.MV_OK == nRet)
                    {
                        AddLog($"图像已保存: {strPath}");
                    }
                    else
                    {
                        AddLog($"保存图像失败! nRet = 0x{nRet:X}");
                    }

                    m_pCamera.MV_CC_FreeImageBuffer_NET(ref stFrameOut);
                }
                else
                {
                    AddLog($"抓取图像失败! nRet = 0x{nRet:X}");
                }
            }
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Clear();
        }

        private void ReceiveThread()
        {
            m_hReceiveThreadStarted.Set();

            MyCamera.MV_FRAME_OUT stFrameOut = new MyCamera.MV_FRAME_OUT();

            while (m_bGrabbing)
            {
                try
                {
                    int nRet = m_pCamera.MV_CC_GetImageBuffer_NET(ref stFrameOut, 1000);
                    if (nRet == MyCamera.MV_OK)
                    {
                        m_nFrameCount++;

                        uint nWidth = stFrameOut.stFrameInfo.nWidth;
                        uint nHeight = stFrameOut.stFrameInfo.nHeight;
                        IntPtr pImageAddr = stFrameOut.stFrameOut.pImageAddr;

                        Dispatcher.Invoke(() =>
                        {
                            FrameCountText.Text = $"帧数: {m_nFrameCount}";

                            if (pImageAddr != IntPtr.Zero)
                            {
                                try
                                {
                                    int width = (int)nWidth;
                                    int height = (int)nHeight;

                                    BitmapSource bitmap = BitmapSource.Create(
                                        width, height, 96, 96,
                                        PixelFormats.Gray8, null,
                                        pImageAddr, width * height, width);

                                    CameraDisplay.Source = bitmap;
                                }
                                catch
                                {
                                }
                            }
                        });

                        m_pCamera.MV_CC_FreeImageBuffer_NET(ref stFrameOut);
                    }
                }
                catch
                {
                }

                Thread.Sleep(10);
            }
        }

        private void AddLog(string message)
        {
            string logEntry = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText(logEntry + "\n");
                LogTextBox.ScrollToEnd();
            });
        }

        private void VisionInspectionPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (m_bGrabbing)
            {
                m_bGrabbing = false;
                if (m_hReceiveThread != null && m_hReceiveThread.IsAlive)
                {
                    m_hReceiveThread.Join(1000);
                }
            }

            if (m_pCamera != null)
            {
                m_pCamera.MV_CC_StopGrabbing_NET();
                m_pCamera.MV_CC_CloseDevice_NET();
                m_pCamera.MV_CC_DestroyDevice_NET();
                m_pCamera = null;
            }
        }
    }
}
