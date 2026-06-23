using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using MvCamCtrl.NET;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Text;

namespace WpfApp1
{
    public partial class VisionInspectionPage : Page
    {
        private MyCamera.MV_CC_DEVICE_INFO_LIST m_pDeviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
        private MyCamera m_pCamera = new MyCamera();
        private bool m_bGrabbing = false;
        private Thread m_hReceiveThread = null;
        private string m_deviceSerial = "";
        private IntPtr m_hwnd = IntPtr.Zero;

        public VisionInspectionPage()
        {
            InitializeComponent();
            Loaded += VisionInspectionPage_Loaded;
            Unloaded += VisionInspectionPage_Unloaded;
        }

        private void VisionInspectionPage_Loaded(object sender, RoutedEventArgs e)
        {
            m_hwnd = ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(this)).Handle;
        }

        private void VisionInspectionPage_Unloaded(object sender, RoutedEventArgs e)
        {
            StopGrab();
            CloseCamera();
        }

        public void SetCameraConfig(string serial, uint deviceType)
        {
            m_deviceSerial = serial;
            LogMessage($"[配置] 序列号: {serial}, 类型: {deviceType}");
        }

        private void StartCameraButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_bGrabbing)
            {
                StopGrab();
                CloseCamera();
                StartCameraButton.Content = "开始采集";
                return;
            }

            StartCameraButton.Content = "连接中...";
            LogMessage("[相机] 开始连接...");

            int nRet;

            // 初始化SDK
            nRet = MyCamera.MV_CC_Initialize_NET();
            if (MyCamera.MV_OK != nRet)
            {
                LogMessage($"[错误] 初始化SDK失败: 0x{nRet:X}");
                StartCameraButton.Content = "开始采集";
                return;
            }

            // 枚举设备
            m_pDeviceList.nDeviceNum = 0;
            nRet = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, ref m_pDeviceList);
            if (MyCamera.MV_OK != nRet)
            {
                LogMessage($"[错误] 枚举设备失败: 0x{nRet:X}");
                MyCamera.MV_CC_Finalize_NET();
                StartCameraButton.Content = "开始采集";
                return;
            }

            LogMessage($"[发现] 设备数量: {m_pDeviceList.nDeviceNum}");

            if (m_pDeviceList.nDeviceNum == 0)
            {
                LogMessage("[错误] 未发现设备");
                MyCamera.MV_CC_Finalize_NET();
                StartCameraButton.Content = "开始采集";
                return;
            }

            // 查找目标设备
            MyCamera.MV_CC_DEVICE_INFO device = new MyCamera.MV_CC_DEVICE_INFO();
            int nDeviceIndex = -1;

            for (int i = 0; i < m_pDeviceList.nDeviceNum; i++)
            {
                IntPtr pDeviceInfo = Marshal.UnsafeAddrOfPinnedArrayElement(m_pDeviceList.pDeviceInfo, i);
                device = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));

                string serial = GetDeviceSerial(device);
                LogMessage($"[设备{i}] 类型: {device.nTLayerType}, 序列号: {serial}");

                if (serial == m_deviceSerial)
                {
                    nDeviceIndex = i;
                    break;
                }
            }

            if (nDeviceIndex < 0)
            {
                LogMessage($"[错误] 未找到序列号为 {m_deviceSerial} 的相机");
                MyCamera.MV_CC_Finalize_NET();
                StartCameraButton.Content = "开始采集";
                return;
            }

            // 创建设备
            nRet = m_pCamera.MV_CC_CreateDevice_NET(ref device);
            if (MyCamera.MV_OK != nRet)
            {
                LogMessage($"[错误] 创建设备失败: 0x{nRet:X}");
                MyCamera.MV_CC_Finalize_NET();
                StartCameraButton.Content = "开始采集";
                return;
            }

            // 打开设备
            nRet = m_pCamera.MV_CC_OpenDevice_NET(MyCamera.MV_ACCESS_Exclusive, 5);
            if (MyCamera.MV_OK != nRet)
            {
                LogMessage($"[错误] 打开设备失败: 0x{nRet:X}");
                m_pCamera.MV_CC_DestroyDevice_NET();
                MyCamera.MV_CC_Finalize_NET();
                StartCameraButton.Content = "开始采集";
                return;
            }

            LogMessage("[成功] 相机已连接");

            // 开始采集
            nRet = m_pCamera.MV_CC_StartGrabbing_NET();
            if (MyCamera.MV_OK != nRet)
            {
                LogMessage($"[错误] 开始采集失败: 0x{nRet:X}");
                m_pCamera.MV_CC_CloseDevice_NET();
                m_pCamera.MV_CC_DestroyDevice_NET();
                MyCamera.MV_CC_Finalize_NET();
                StartCameraButton.Content = "开始采集";
                return;
            }

            m_bGrabbing = true;
            m_hReceiveThread = new Thread(ReceiveThreadProcess);
            m_hReceiveThread.IsBackground = true;
            m_hReceiveThread.Start();

            StartCameraButton.Content = "停止采集";
            LogMessage("[成功] 开始采集");
        }

        private void ReceiveThreadProcess()
        {
            int nRet;
            MyCamera.MV_FRAME_OUT stFrameOut = new MyCamera.MV_FRAME_OUT();
            MyCamera.MV_FRAME_OUT stFrameInfo = new MyCamera.MV_FRAME_OUT();
            uint nFrameCount = 0;

            while (m_bGrabbing)
            {
                nRet = m_pCamera.MV_CC_GetImageBuffer_NET(ref stFrameOut, 1000);
                if (nRet == MyCamera.MV_OK)
                {
                    nFrameCount++;
                    uint nWidth = stFrameOut.nWidth;
                    uint nHeight = stFrameOut.nHeight;
                    uint nFrameLen = stFrameOut.nFrameLen;
                    uint enPixelType = stFrameOut.enPixelType;

                    // 显示图像
                    m_pCamera.MV_CC_Display_NET(stFrameOut.pBufAddr);

                    // 更新帧数
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        FrameCountText.Text = nFrameCount.ToString();
                    }));

                    // 释放缓存
                    m_pCamera.MV_CC_FreeImageBuffer_NET(ref stFrameOut);

                    if (nFrameCount % 30 == 0)
                    {
                        LogMessage($"[采集] 帧数: {nFrameCount}, 分辨率: {nWidth}x{nHeight}");
                    }
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
        }

        private void StopGrab()
        {
            if (m_bGrabbing)
            {
                m_bGrabbing = false;
                Thread.Sleep(500);

                if (m_hReceiveThread != null && m_hReceiveThread.IsAlive)
                {
                    m_hReceiveThread.Join(1000);
                }

                m_pCamera.MV_CC_StopGrabbing_NET();
                LogMessage("[相机] 已停止采集");
            }
        }

        private void CloseCamera()
        {
            if (m_pCamera != null)
            {
                m_pCamera.MV_CC_CloseDevice_NET();
                m_pCamera.MV_CC_DestroyDevice_NET();
                MyCamera.MV_CC_Finalize_NET();
                LogMessage("[相机] 已断开连接");
            }
        }

        private string GetDeviceSerial(MyCamera.MV_CC_DEVICE_INFO device)
        {
            try
            {
                if (device.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                {
                    IntPtr pInfo = Marshal.UnsafeAddrOfPinnedArrayElement(device.SpecialInfo.stGigEInfo, 0);
                    MyCamera.MV_GIGE_DEVICE_INFO gigeInfo = (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(pInfo, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                    return Encoding.ASCII.GetString(gigeInfo.chSerialNumber).TrimEnd('\0');
                }
                else if (device.nTLayerType == MyCamera.MV_USB_DEVICE)
                {
                    IntPtr pInfo = Marshal.UnsafeAddrOfPinnedArrayElement(device.SpecialInfo.stUsb3VInfo, 0);
                    MyCamera.MV_USB3_DEVICE_INFO usbInfo = (MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(pInfo, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                    return Encoding.ASCII.GetString(usbInfo.chSerialNumber).TrimEnd('\0');
                }
            }
            catch (Exception ex)
            {
                LogMessage($"[错误] 获取序列号失败: {ex.Message}");
            }
            return "";
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Clear();
        }

        private void LogMessage(string message)
        {
            try
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    string timestamp = DateTime.Now.ToString("HH:mm:ss");
                    LogTextBox.AppendText($"[{timestamp}] {message}\n");
                    LogTextBox.ScrollToEnd();
                }));
            }
            catch { }
        }
    }
}
