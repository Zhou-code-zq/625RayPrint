using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MvCamCtrl.NET;

namespace WpfApp1
{
    public partial class VisionInspectionPage : Page
    {
        // 海康相机SDK相关变量
        private MyCamera m_pCamera;
        private MyCamera.MV_FRAME_OUT m_Frame;
        private bool m_bGrabbing = false;
        private int m_nFrames = 0;
        
        // 相机配置参数
        private string m_strCameraSerial = "";
        private uint m_nDeviceType = 0;
        private string m_strCameraIp = "";
        
        // 锁
        private readonly object m_locker = new object();

        public VisionInspectionPage()
        {
            InitializeComponent();
        }

        // 加载配置
        public void SetCameraConfig(string serial, uint deviceType, string ip = "")
        {
            m_strCameraSerial = serial;
            m_nDeviceType = deviceType;
            m_strCameraIp = ip;
            AddLog($"相机配置: 序列号={serial}, 类型={deviceType}, IP={ip}");
        }

        // 开始采集按钮
        private void StartCameraButton_Click(object sender, RoutedEventArgs e)
        {
            lock (m_locker)
            {
                if (m_bGrabbing)
                {
                    // 停止采集
                    StopGrabbing();
                }
                else
                {
                    // 开始采集
                    StartGrabbing();
                }
            }
        }

        // 保存图像按钮
        private void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentImage();
        }

        // 清空日志按钮
        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Clear();
        }

        // 开始采集
        private void StartGrabbing()
        {
            AddLog("正在连接相机...");
            StatusText.Text = "连接中...";

            // 初始化SDK
            int nRet = MyCamera.MV_CC_Initialize_NET();
            if (nRet != MyCamera.MV_OK)
            {
                AddLog($"SDK初始化失败，错误码: {nRet:X8}");
                StatusText.Text = "初始化失败";
                return;
            }

            // 枚举设备
            MyCamera.MV_CC_DEVICE_INFO_LIST stDeviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
            nRet = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, ref stDeviceList);
            if (nRet != MyCamera.MV_OK)
            {
                AddLog($"枚举设备失败，错误码: {nRet:X8}");
                StatusText.Text = "枚举失败";
                return;
            }

            if (stDeviceList.nDeviceNum == 0)
            {
                AddLog("未发现任何相机设备");
                StatusText.Text = "未发现设备";
                return;
            }

            AddLog($"发现 {stDeviceList.nDeviceNum} 个设备");

            // 查找目标相机
            IntPtr pDeviceInfo = IntPtr.Zero;
            bool bFound = false;
            for (int i = 0; i < (int)stDeviceList.nDeviceNum; i++)
            {
                pDeviceInfo = Marshal.UnsafeAddrOfPinnedArrayElement(stDeviceList.pDeviceInfo, i);
                MyCamera.MV_CC_DEVICE_INFO deviceInfo = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));

                // 获取序列号
                string serial = GetDeviceSerial(deviceInfo);
                AddLog($"  设备{i}: 序列号={serial}");

                if (serial == m_strCameraSerial)
                {
                    bFound = true;
                    AddLog($"找到目标相机: {serial}");
                    break;
                }
            }

            if (!bFound)
            {
                AddLog($"未找到序列号为 {m_strCameraSerial} 的相机");
                StatusText.Text = "未找到相机";
                return;
            }

            // 创建设备
            m_pCamera = new MyCamera();
            nRet = m_pCamera.MV_CC_CreateDevice_NET(ref deviceInfo);
            if (nRet != MyCamera.MV_OK)
            {
                AddLog($"创建设备失败，错误码: {nRet:X8}");
                StatusText.Text = "创建失败";
                return;
            }

            // 打开设备
            nRet = m_pCamera.MV_CC_OpenDevice_NET(MyCamera.MV_ACCESS_Exclusive, 0);
            if (nRet != MyCamera.MV_OK)
            {
                AddLog($"打开设备失败，错误码: {nRet:X8}");
                m_pCamera.MV_CC_DestroyDevice_NET();
                StatusText.Text = "打开失败";
                return;
            }

            // 开始采集
            nRet = m_pCamera.MV_CC_StartGrabbing_NET();
            if (nRet != MyCamera.MV_OK)
            {
                AddLog($"开始采集失败，错误码: {nRet:X8}");
                m_pCamera.MV_CC_CloseDevice_NET();
                m_pCamera.MV_CC_DestroyDevice_NET();
                StatusText.Text = "采集失败";
                return;
            }

            m_bGrabbing = true;
            m_nFrames = 0;
            StartCameraButton.Content = "停止采集";
            StatusText.Text = "采集中...";
            AddLog("相机连接成功，开始采集");

            // 启动显示线程
            System.Threading.Thread displayThread = new System.Threading.Thread(DisplayThread);
            displayThread.IsBackground = true;
            displayThread.Start();
        }

        // 停止采集
        private void StopGrabbing()
        {
            if (m_pCamera != null)
            {
                AddLog("正在停止采集...");

                // 停止采集
                m_pCamera.MV_CC_StopGrabbing_NET();

                // 关闭设备
                m_pCamera.MV_CC_CloseDevice_NET();

                // 销毁设备
                m_pCamera.MV_CC_DestroyDevice_NET();

                m_pCamera = null;
            }

            m_bGrabbing = false;
            StartCameraButton.Content = "开始采集";
            StatusText.Text = "已断开";
            AddLog("相机已断开");
        }

        // 显示线程
        private void DisplayThread()
        {
            while (m_bGrabbing)
            {
                lock (m_locker)
                {
                    if (m_pCamera != null && m_bGrabbing)
                    {
                        m_Frame = new MyCamera.MV_FRAME_OUT();
                        int nRet = m_pCamera.MV_CC_GetImageBuffer_NET(ref m_Frame, 100);
                        if (nRet == MyCamera.MV_OK)
                        {
                            // 显示图像
                            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                DisplayImage();
                            }));

                            m_nFrames++;
                        }
                    }
                }
                System.Threading.Thread.Sleep(30);
            }
        }

        // 显示图像
        private void DisplayImage()
        {
            try
            {
                // 获取图像信息
                int nWidth = (int)m_Frame.nWidth;
                int nHeight = (int)m_Frame.nHeight;
                IntPtr pImageAddr = m_Frame.pBufAddr;
                uint nFrameLen = m_Frame.nFrameLen;
                uint enPixelType = m_Frame.enPixelType;

                if (nWidth <= 0 || nHeight <= 0 || pImageAddr == IntPtr.Zero)
                    return;

                // 更新帧计数
                m_nFrames++;
                StatusText.Text = $"采集中... 帧数: {m_nFrames}";

                // 创建BitmapSource
                BitmapSource bitmapSource = null;

                // 根据像素格式创建图像
                if (enPixelType == (uint)MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8)
                {
                    // 黑白图像
                    bitmapSource = BitmapSource.Create(nWidth, nHeight, 96, 96,
                        PixelFormats.Gray8, null, pImageAddr, (int)nFrameLen, nWidth);
                }
                else if (enPixelType == (uint)MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8_Packed)
                {
                    // RGB图像
                    bitmapSource = BitmapSource.Create(nWidth, nHeight, 96, 96,
                        PixelFormats.Rgb24, null, pImageAddr, (int)nFrameLen, nWidth * 3);
                }
                else
                {
                    // 其他格式，先尝试当作RGB处理
                    bitmapSource = BitmapSource.Create(nWidth, nHeight, 96, 96,
                        PixelFormats.Rgb24, null, pImageAddr, (int)nFrameLen, nWidth * 3);
                }

                if (bitmapSource != null)
                {
                    bitmapSource.Freeze();
                    CameraImage.Source = bitmapSource;
                }

                // 释放缓存
                if (m_pCamera != null)
                {
                    m_pCamera.MV_CC_FreeImageBuffer_NET(ref m_Frame);
                }
            }
            catch (Exception ex)
            {
                AddLog($"显示图像异常: {ex.Message}");
            }
        }

        // 保存当前图像
        private void SaveCurrentImage()
        {
            if (m_pCamera == null || !m_bGrabbing)
            {
                AddLog("请先连接相机");
                return;
            }

            try
            {
                // 获取一帧图像
                MyCamera.MV_FRAME_OUT frameOut = new MyCamera.MV_FRAME_OUT();
                int nRet = m_pCamera.MV_CC_GetImageBuffer_NET(ref frameOut, 1000);
                if (nRet != MyCamera.MV_OK)
                {
                    AddLog($"获取图像失败，错误码: {nRet:X8}");
                    return;
                }

                // 创建图像
                int nWidth = (int)frameOut.nWidth;
                int nHeight = (int)frameOut.nHeight;
                IntPtr pImageAddr = frameOut.pBufAddr;
                uint enPixelType = frameOut.enPixelType;

                BitmapSource bitmapSource = null;
                if (enPixelType == (uint)MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8)
                {
                    bitmapSource = BitmapSource.Create(nWidth, nHeight, 96, 96,
                        PixelFormats.Gray8, null, pImageAddr, (int)frameOut.nFrameLen, nWidth);
                }
                else
                {
                    bitmapSource = BitmapSource.Create(nWidth, nHeight, 96, 96,
                        PixelFormats.Rgb24, null, pImageAddr, (int)frameOut.nFrameLen, nWidth * 3);
                }

                // 保存图像
                string fileName = $"Image_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);

                using (FileStream fs = new FileStream(filePath, FileMode.Create))
                {
                    BitmapEncoder encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                    encoder.Save(fs);
                }

                AddLog($"图像已保存: {filePath}");

                // 释放缓存
                m_pCamera.MV_CC_FreeImageBuffer_NET(ref frameOut);
            }
            catch (Exception ex)
            {
                AddLog($"保存图像异常: {ex.Message}");
            }
        }

        // 获取设备序列号
        private string GetDeviceSerial(MyCamera.MV_CC_DEVICE_INFO deviceInfo)
        {
            try
            {
                if (deviceInfo.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                {
                    IntPtr pGigEInfo = Marshal.UnsafeAddrOfPinnedArrayElement(deviceInfo.SpecialInfo.stGigEInfo, 0);
                    MyCamera.MV_GIGE_DEVICE_INFO gigEInfo = (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(pGigEInfo, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                    
                    // 序列号在stGigEInfo.chSerialNumber中
                    byte[] serialBytes = gigEInfo.chSerialNumber;
                    int nullIndex = Array.IndexOf(serialBytes, (byte)0);
                    if (nullIndex > 0)
                        return System.Text.Encoding.ASCII.GetString(serialBytes, 0, nullIndex);
                    return System.Text.Encoding.ASCII.GetString(serialBytes);
                }
                else if (deviceInfo.nTLayerType == MyCamera.MV_USB_DEVICE)
                {
                    IntPtr pUsbInfo = Marshal.UnsafeAddrOfPinnedArrayElement(deviceInfo.SpecialInfo.stUsb3VInfo, 0);
                    MyCamera.MV_USB3_DEVICE_INFO usbInfo = (MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(pUsbInfo, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                    
                    byte[] serialBytes = usbInfo.chSerialNumber;
                    int nullIndex = Array.IndexOf(serialBytes, (byte)0);
                    if (nullIndex > 0)
                        return System.Text.Encoding.ASCII.GetString(serialBytes, 0, nullIndex);
                    return System.Text.Encoding.ASCII.GetString(serialBytes);
                }
            }
            catch (Exception ex)
            {
                AddLog($"获取序列号异常: {ex.Message}");
            }
            return "";
        }

        // 添加日志
        private void AddLog(string message)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                string log = $"[{DateTime.Now:HH:mm:ss}] {message}";
                LogTextBox.AppendText(log + Environment.NewLine);
                LogTextBox.ScrollToEnd();
            }));
        }
    }
}
