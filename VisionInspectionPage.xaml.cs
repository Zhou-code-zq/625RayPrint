using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using MvCamCtrl.NET;

namespace WpfApp1
{
    public partial class VisionInspectionPage : Page
    {
        private MyCamera m_pCamera;
        private bool m_bGrabbing = false;
        private Thread m_hReceiveThread = null;
        private string m_cameraSerial = "";
        private uint m_deviceType = 0;
        private IntPtr m_ImageBuffer = IntPtr.Zero;
        private UInt32 m_nDataSize = 0;
        private ManualResetEvent m_hGrabEvent = new ManualResetEvent(false);
        private bool m_isOperating = false;

        public VisionInspectionPage()
        {
            InitializeComponent();
        }

        public void SetCameraConfig(string serial, uint deviceType)
        {
            m_cameraSerial = serial;
            m_deviceType = deviceType;
            Application.Current.Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 配置相机: 序列号={serial}, 类型={deviceType}\n");
                LogTextBox.ScrollToEnd();
            });
        }

        private void StartCameraButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_isOperating)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] 正在操作中，请稍候...\n");
                    LogTextBox.ScrollToEnd();
                });
                return;
            }

            if (m_bGrabbing)
            {
                StopGrabbing();
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StartCameraButton.Content = "开始采集";
                });
                return;
            }

            m_isOperating = true;
            Application.Current.Dispatcher.Invoke(() =>
            {
                StartCameraButton.Content = "连接中...";
            });

            Thread connectThread = new Thread(ConnectAndStartGrabbing);
            connectThread.IsBackground = true;
            connectThread.Start();
        }

        private void ConnectAndStartGrabbing()
        {
            try
            {
                // 初始化SDK
                int nRet = MyCamera.MV_CC_Initialize_NET();
                if (nRet != MyCamera.MV_OK)
                {
                    Log($"初始化SDK失败: 0x{nRet:X}");
                    m_isOperating = false;
                    Application.Current.Dispatcher.Invoke(() => { StartCameraButton.Content = "开始采集"; });
                    return;
                }

                // 枚举设备
                MyCamera.MV_CC_DEVICE_INFO_LIST m_pDeviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
                uint nDeviceType = MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE;
                nRet = MyCamera.MV_CC_EnumDevices_NET(nDeviceType, ref m_pDeviceList);
                if (nRet != MyCamera.MV_OK)
                {
                    Log($"枚举设备失败: 0x{nRet:X}");
                    MyCamera.MV_CC_Finalize_NET();
                    m_isOperating = false;
                    Application.Current.Dispatcher.Invoke(() => { StartCameraButton.Content = "开始采集"; });
                    return;
                }

                if (m_pDeviceList.nDeviceNum == 0)
                {
                    Log("未发现任何设备");
                    MyCamera.MV_CC_Finalize_NET();
                    m_isOperating = false;
                    Application.Current.Dispatcher.Invoke(() => { StartCameraButton.Content = "开始采集"; });
                    return;
                }

                // 查找指定序列号的设备
                IntPtr pDeviceInfo = IntPtr.Zero;
                for (int i = 0; i < m_pDeviceList.nDeviceNum; i++)
                {
                    pDeviceInfo = Marshal.AddrOfPinnedArrayElement(m_pDeviceList.pDeviceInfo, i);
                    MyCamera.MV_CC_DEVICE_INFO deviceInfo = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));

                    string serial = GetDeviceSerial(deviceInfo);
                    Log($"发现设备[{i}]: 序列号={serial}");

                    if (serial == m_cameraSerial)
                    {
                        Log($"找到目标相机: {serial}");
                        break;
                    }
                }

                if (pDeviceInfo == IntPtr.Zero)
                {
                    Log($"未找到序列号为 {m_cameraSerial} 的相机");
                    MyCamera.MV_CC_Finalize_NET();
                    m_isOperating = false;
                    Application.Current.Dispatcher.Invoke(() => { StartCameraButton.Content = "开始采集"; });
                    return;
                }

                // 创建设备
                m_pCamera = new MyCamera();
                nRet = m_pCamera.MV_CC_CreateDevice_NET(ref deviceInfo);
                if (nRet != MyCamera.MV_OK)
                {
                    Log($"创建设备失败: 0x{nRet:X}");
                    MyCamera.MV_CC_Finalize_NET();
                    m_isOperating = false;
                    Application.Current.Dispatcher.Invoke(() => { StartCameraButton.Content = "开始采集"; });
                    return;
                }

                // 打开设备
                nRet = m_pCamera.MV_CC_OpenDevice_NET(MyCamera.MV_ACCESS_Exclusive, 0);
                if (nRet != MyCamera.MV_OK)
                {
                    Log($"打开设备失败: 0x{nRet:X}");
                    m_pCamera.MV_CC_DestroyDevice_NET();
                    MyCamera.MV_CC_Finalize_NET();
                    m_isOperating = false;
                    Application.Current.Dispatcher.Invoke(() => { StartCameraButton.Content = "开始采集"; });
                    return;
                }

                // 获取图像缓存大小
                MyCamera.MV_CC_DEVICE_INFO deviceInfoForSize = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));
                UInt32 nWidth = 2448;
                UInt32 nHeight = 2048;
                
                try
                {
                    if (deviceInfoForSize.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                    {
                        IntPtr ptrGigE = Marshal.AddrOfPinnedArrayElement(deviceInfoForSize.SpecialInfo.stGigEInfo, 0);
                        MyCamera.MV_GIGE_DEVICE_INFO gigeInfo = (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(ptrGigE, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                        nWidth = gigeInfo.nWidth;
                        nHeight = gigeInfo.nHeight;
                    }
                    else if (deviceInfoForSize.nTLayerType == MyCamera.MV_USB_DEVICE)
                    {
                        IntPtr ptrUSB = Marshal.AddrOfPinnedArrayElement(deviceInfoForSize.SpecialInfo.stUsb3VInfo, 0);
                        MyCamera.MV_USB3_DEVICE_INFO usbInfo = (MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(ptrUSB, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                        nWidth = usbInfo.nWidth;
                        nHeight = usbInfo.nHeight;
                    }
                }
                catch { }

                m_nDataSize = nWidth * nHeight * 3 + 2048;
                if (m_ImageBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(m_ImageBuffer);
                }
                m_ImageBuffer = Marshal.AllocHGlobal((int)m_nDataSize);

                // 开始采集
                nRet = m_pCamera.MV_CC_StartGrabbing_NET();
                if (nRet != MyCamera.MV_OK)
                {
                    Log($"开始采集失败: 0x{nRet:X}");
                    m_pCamera.MV_CC_CloseDevice_NET();
                    m_pCamera.MV_CC_DestroyDevice_NET();
                    MyCamera.MV_CC_Finalize_NET();
                    m_isOperating = false;
                    Application.Current.Dispatcher.Invoke(() => { StartCameraButton.Content = "开始采集"; });
                    return;
                }

                m_bGrabbing = true;
                m_hGrabEvent.Reset();

                // 启动接收线程
                m_hReceiveThread = new Thread(ReceiveThreadProcess);
                m_hReceiveThread.IsBackground = true;
                m_hReceiveThread.Start();

                Log("相机连接成功，开始采集");
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StartCameraButton.Content = "停止采集";
                });
            }
            catch (Exception ex)
            {
                Log($"连接异常: {ex.Message}");
            }
            finally
            {
                m_isOperating = false;
            }
        }

        private void ReceiveThreadProcess()
        {
            MyCamera.MV_FRAME_OUT_INFO stFrameInfo = new MyCamera.MV_FRAME_OUT_INFO();
            int nFrameCount = 0;

            while (m_bGrabbing)
            {
                int nRet = m_pCamera.MV_CC_GetOneFrame_NET(m_ImageBuffer, m_nDataSize, ref stFrameInfo);
                if (nRet == MyCamera.MV_OK)
                {
                    nFrameCount++;
                    try
                    {
                        // 直接使用stFrameInfo的字段
                        UInt32 nWidth = stFrameInfo.nWidth;
                        UInt32 nHeight = stFrameInfo.nHeight;
                        UInt32 nFrameLen = stFrameInfo.nFrameLen;
                        MyCamera.MvGvspPixelType enPixelType = stFrameInfo.enPixelType;

                        Application.Current.Dispatcher.BeginInvoke(() =>
                        {
                            try
                            {
                                FrameCountText.Text = nFrameCount.ToString();
                                DisplayImage(m_ImageBuffer, (int)nWidth, (int)nHeight, enPixelType);
                            }
                            catch { }
                        });
                    }
                    catch { }
                }
                Thread.Sleep(10);
            }
        }

        private void StopGrabbing()
        {
            if (!m_bGrabbing) return;

            m_isOperating = true;
            m_bGrabbing = false;
            m_hGrabEvent.Set();

            Thread stopThread = new Thread(() =>
            {
                try
                {
                    if (m_hReceiveThread != null && m_hReceiveThread.IsAlive)
                    {
                        m_hReceiveThread.Join(1000);
                    }

                    if (m_pCamera != null)
                    {
                        m_pCamera.MV_CC_StopGrabbing_NET();
                        m_pCamera.MV_CC_CloseDevice_NET();
                        m_pCamera.MV_CC_DestroyDevice_NET();
                        m_pCamera = null;
                    }

                    MyCamera.MV_CC_Finalize_NET();

                    if (m_ImageBuffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(m_ImageBuffer);
                        m_ImageBuffer = IntPtr.Zero;
                    }

                    Log("相机已断开");
                }
                catch (Exception ex)
                {
                    Log($"断开异常: {ex.Message}");
                }
                finally
                {
                    m_isOperating = false;
                }
            });
            stopThread.IsBackground = true;
            stopThread.Start();
        }

        private void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_ImageBuffer == IntPtr.Zero || !m_bGrabbing)
            {
                Log("请先连接相机并开始采集");
                return;
            }

            Thread saveThread = new Thread(() =>
            {
                try
                {
                    // 获取当前帧信息
                    MyCamera.MV_FRAME_OUT_INFO stFrameInfo = new MyCamera.MV_FRAME_OUT_INFO();
                    int nRet = m_pCamera.MV_CC_GetOneFrame_NET(m_ImageBuffer, m_nDataSize, ref stFrameInfo);
                    if (nRet != MyCamera.MV_OK)
                    {
                        Log("保存失败: 无法获取图像");
                        return;
                    }

                    string savePath = System.Environment.CurrentDirectory + $"\\Images\\{DateTime.Now:yyyyMMdd_HHmmss}.bmp";
                    Directory.CreateDirectory(Path.GetDirectoryName(savePath));

                    // 保存为BMP
                    SaveBitmap(m_ImageBuffer, (int)stFrameInfo.nWidth, (int)stFrameInfo.nHeight, stFrameInfo.enPixelType, savePath);
                    Log($"图像已保存: {savePath}");
                }
                catch (Exception ex)
                {
                    Log($"保存异常: {ex.Message}");
                }
            });
            saveThread.IsBackground = true;
            saveThread.Start();
        }

        private void DisplayImage(IntPtr pData, int nWidth, int nHeight, MyCamera.MvGvspPixelType enPixelType)
        {
            try
            {
                PixelFormat pixelFormat;
                int bytesPerPixel;

                switch (enPixelType)
                {
                    case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8:
                        pixelFormat = PixelFormats.Gray8;
                        bytesPerPixel = 1;
                        break;
                    case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono16:
                        pixelFormat = PixelFormats.Gray16;
                        bytesPerPixel = 2;
                        break;
                    default:
                        pixelFormat = PixelFormats.Bgr24;
                        bytesPerPixel = 3;
                        break;
                }

                int stride = nWidth * bytesPerPixel;
                byte[] data = new byte[nWidth * nHeight * bytesPerPixel];
                Marshal.Copy(pData, data, 0, data.Length);

                BitmapSource bitmap = BitmapSource.Create(
                    nWidth, nHeight, 96, 96, pixelFormat, null, data, stride);
                bitmap.Freeze();
                CameraDisplay.Source = bitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"显示图像异常: {ex.Message}");
            }
        }

        private void SaveBitmap(IntPtr pData, int nWidth, int nHeight, MyCamera.MvGvspPixelType enPixelType, string savePath)
        {
            try
            {
                PixelFormat pixelFormat;
                int bytesPerPixel;

                switch (enPixelType)
                {
                    case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8:
                        pixelFormat = PixelFormats.Gray8;
                        bytesPerPixel = 1;
                        break;
                    case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono16:
                        pixelFormat = PixelFormats.Gray16;
                        bytesPerPixel = 2;
                        break;
                    default:
                        pixelFormat = PixelFormats.Bgr24;
                        bytesPerPixel = 3;
                        break;
                }

                int stride = nWidth * bytesPerPixel;
                byte[] data = new byte[nWidth * nHeight * bytesPerPixel];
                Marshal.Copy(pData, data, 0, data.Length);

                BitmapSource bitmap = BitmapSource.Create(
                    nWidth, nHeight, 96, 96, pixelFormat, null, data, stride);
                bitmap.Freeze();

                JpegBitmapEncoder encoder = new JpegBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream fs = new FileStream(savePath, FileMode.Create))
                {
                    encoder.Save(fs);
                }
            }
            catch (Exception ex)
            {
                Log($"保存BMP异常: {ex.Message}");
            }
        }

        private string GetDeviceSerial(MyCamera.MV_CC_DEVICE_INFO deviceInfo)
        {
            try
            {
                if (deviceInfo.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                {
                    IntPtr ptr = Marshal.AddrOfPinnedArrayElement(deviceInfo.SpecialInfo.stGigEInfo, 0);
                    MyCamera.MV_GIGE_DEVICE_INFO gigeInfo = (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(ptr, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                    return Encoding.ASCII.GetString(gigeInfo.chSerialNumber).TrimEnd('\0');
                }
                else if (deviceInfo.nTLayerType == MyCamera.MV_USB_DEVICE)
                {
                    IntPtr ptr = Marshal.AddrOfPinnedArrayElement(deviceInfo.SpecialInfo.stUsb3VInfo, 0);
                    MyCamera.MV_USB3_DEVICE_INFO usbInfo = (MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(ptr, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                    return Encoding.ASCII.GetString(usbInfo.chSerialNumber).TrimEnd('\0');
                }
            }
            catch { }
            return "";
        }

        private void Log(string message)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                LogTextBox.ScrollToEnd();
            });
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                LogTextBox.Clear();
            });
        }
    }
}
